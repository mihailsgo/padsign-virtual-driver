using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Padsign.Listener;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var baseDir = AppContext.BaseDirectory;
        var configPath = args.Length > 0 ? args[0] : Path.Combine(baseDir, "padsign.json");
        var logger = new FileLogger(Path.Combine(baseDir, "logs", "padsign.log"));

        PadsignConfig config;
        try
        {
            config = PadsignConfig.Load(configPath);
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to load config from {configPath}: {ex}");
            return 1;
        }

        logger.Info("Padsign listener starting...");
        logger.Info($"Listening on TCP port {config.Port}, working dir: {config.WorkingDirectory}");

        var processor = new JobProcessor(config, logger);
        var server = new RawPrintServer(config, logger, processor);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            logger.Info("Stopping (Ctrl+C)...");
        };

        try
        {
            await server.RunAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        catch (Exception ex)
        {
            logger.Error($"Fatal error: {ex}");
            return 1;
        }

        logger.Info("Padsign listener stopped.");
        return 0;
    }
}

internal sealed record PadsignConfig
{
    public string ApiUrl { get; init; } = string.Empty;
    public string AuthenticationHeaderName { get; init; } = "Authorization";
    public string AuthenticationHeaderValue { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string Company { get; init; } = string.Empty;
    public int Port { get; init; } = 9100;
    public string WorkingDirectory { get; init; } = "spool";
    public int UploadTimeoutSeconds { get; init; } = 30;
    public int MaxUploadRetries { get; init; } = 3;
    public int RetryBackoffSeconds { get; init; } = 2;
    public bool CleanupOnSuccess { get; init; }

    public static PadsignConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Config not found", path);

        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<PadsignConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        if (cfg == null)
            throw new InvalidOperationException("Could not deserialize config");
        if (string.IsNullOrWhiteSpace(cfg.ApiUrl))
            throw new InvalidOperationException("ApiUrl missing in config");
        var authHeaderValue = cfg.AuthenticationHeaderValue;
        if (string.IsNullOrWhiteSpace(authHeaderValue) && !string.IsNullOrWhiteSpace(cfg.ApiKey))
            authHeaderValue = $"Bearer {cfg.ApiKey}";
        if (string.IsNullOrWhiteSpace(authHeaderValue))
            throw new InvalidOperationException("AuthenticationHeaderValue missing in config");
        if (string.IsNullOrWhiteSpace(cfg.Email))
            throw new InvalidOperationException("Email missing in config");
        if (string.IsNullOrWhiteSpace(cfg.Company))
            throw new InvalidOperationException("Company missing in config");
        return cfg with
        {
            AuthenticationHeaderName = string.IsNullOrWhiteSpace(cfg.AuthenticationHeaderName) ? "Authorization" : cfg.AuthenticationHeaderName.Trim(),
            AuthenticationHeaderValue = authHeaderValue.Trim(),
            Email = cfg.Email.Trim(),
            Company = cfg.Company.Trim(),
            WorkingDirectory = ResolvePath(cfg.WorkingDirectory, AppContext.BaseDirectory)
        };
    }

    private static string ResolvePath(string path, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Path.Combine(baseDir, "spool");
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(baseDir, path));
    }
}

internal sealed class RawPrintServer
{
    private readonly PadsignConfig _config;
    private readonly FileLogger _logger;
    private readonly JobProcessor _processor;

    public RawPrintServer(PadsignConfig config, FileLogger logger, JobProcessor processor)
    {
        _config = config;
        _logger = logger;
        _processor = processor;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, _config.Port);
        listener.Start();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var jobId = $"{stamp}-{Guid.NewGuid():N}".Substring(0, 32);

        try
        {
            _logger.Info($"Connection from {remote} job {jobId}");
            await _processor.ProcessAsync(client.GetStream(), jobId, cancellationToken);
            _logger.Info($"Job {jobId} completed.");
        }
        catch (Exception ex)
        {
            _logger.Error($"Job {jobId} failed: {ex}");
        }
        finally
        {
            client.Dispose();
        }
    }
}

internal sealed class JobProcessor
{
    private readonly PadsignConfig _config;
    private readonly FileLogger _logger;

    public JobProcessor(PadsignConfig config, FileLogger logger)
    {
        _config = config;
        _logger = logger;
        Directory.CreateDirectory(_config.WorkingDirectory);
    }

    public async Task ProcessAsync(Stream inputStream, string jobId, CancellationToken cancellationToken)
    {
        var spoolPath = Path.Combine(_config.WorkingDirectory, $"job-{jobId}.prn");

        long bytes;
        await using (var file = File.Create(spoolPath))
        {
            bytes = await CopyWithCountAsync(inputStream, file, cancellationToken);
        }

        _logger.Info($"Job {jobId}: received {bytes} bytes to {spoolPath}");
        if (bytes == 0)
            throw new InvalidOperationException($"Job {jobId}: empty print payload");

        var format = DetectFormat(spoolPath);
        _logger.Info($"Job {jobId}: detected format {format}");

        if (format != JobFormat.Pdf)
        {
            _logger.Error($"Job {jobId}: document is not PDF. Upload request has not been made.");
            return;
        }

        var pdfPath = Path.ChangeExtension(spoolPath, ".pdf");
        File.Copy(spoolPath, pdfPath, overwrite: true);
        _logger.Info($"Job {jobId}: input already PDF, upload request will be sent.");

        await UploadWithRetryAsync(pdfPath, jobId, cancellationToken);

        if (_config.CleanupOnSuccess)
        {
            TryDelete(spoolPath);
            TryDelete(pdfPath);
        }
    }

    private async Task UploadWithRetryAsync(string pdfPath, string jobId, CancellationToken cancellationToken)
    {
        var sessionCombo = $"{_config.Email}|{_config.Company}";
        for (var attempt = 1; attempt <= Math.Max(_config.MaxUploadRetries, 1); attempt++)
        {
            try
            {
                await UploadOnceAsync(pdfPath, jobId, cancellationToken);
                _logger.Info($"Job {jobId}: upload successful for session {sessionCombo}.");
                return;
            }
            catch (Exception ex)
            {
                _logger.Error($"Job {jobId}: upload attempt {attempt} failed for session {sessionCombo}: {ex.Message}");
                if (attempt == _config.MaxUploadRetries)
                    throw;

                var delay = TimeSpan.FromSeconds(_config.RetryBackoffSeconds * attempt);
                _logger.Info($"Job {jobId}: retrying in {delay.TotalSeconds:N0}s ...");
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task UploadOnceAsync(string pdfPath, string jobId, CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(_config.UploadTimeoutSeconds)
        };

        SetAuthenticationHeader(client, _config.AuthenticationHeaderName, _config.AuthenticationHeaderValue);

        await using var fileStream = File.OpenRead(pdfPath);
        using var content = new MultipartFormDataContent();
        var fileName = Path.GetFileName(pdfPath);
        var pdfContent = new StreamContent(fileStream);
        pdfContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");

        content.Add(pdfContent, "file", fileName);
        content.Add(new StringContent(_config.Email), "email");
        content.Add(new StringContent(_config.Company), "company");

        _logger.Info($"Job {jobId}: uploading {fileName} to {_config.ApiUrl} for session {_config.Email}|{_config.Company}");

        var response = await client.PostAsync(_config.ApiUrl, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Upload failed {(int)response.StatusCode} {response.ReasonPhrase}. Body: {Truncate(body, 500)}");
        }

        _logger.Debug($"Job {jobId}: API response {response.StatusCode}: {Truncate(body, 500)}");
    }

    private static async Task<long> CopyWithCountAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
        }
        return total;
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max] + "...";

    private static void SetAuthenticationHeader(HttpClient client, string headerName, string headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerName) || string.IsNullOrWhiteSpace(headerValue))
            return;

        if (headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
        {
            if (AuthenticationHeaderValue.TryParse(headerValue, out var authValue))
            {
                client.DefaultRequestHeaders.Authorization = authValue;
                return;
            }
            throw new InvalidOperationException("AuthenticationHeaderValue is not a valid Authorization header value.");
        }

        client.DefaultRequestHeaders.TryAddWithoutValidation(headerName, headerValue);
    }

    private static JobFormat DetectFormat(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buffer = new byte[Math.Min(8, (int)fs.Length)];
            var read = fs.Read(buffer, 0, buffer.Length);
            var header = Encoding.ASCII.GetString(buffer, 0, read);
            if (header.StartsWith("%PDF-", StringComparison.OrdinalIgnoreCase))
                return JobFormat.Pdf;
            if (header.StartsWith("%!", StringComparison.OrdinalIgnoreCase))
                return JobFormat.PostScript;
            if (header.StartsWith("%!PS", StringComparison.OrdinalIgnoreCase))
                return JobFormat.PostScript;
            return JobFormat.Unknown;
        }
        catch
        {
            return JobFormat.Unknown;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to delete {path}: {ex.Message}");
        }
    }
}

internal enum JobFormat
{
    Unknown,
    Pdf,
    PostScript
}

internal sealed class FileLogger
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileLogger(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
    }

    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERROR", message);
    public void Debug(string message) => Write("DEBUG", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTime.UtcNow:O} [{level}] {message}";
        lock (_gate)
        {
            File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
        }
        Console.WriteLine(line);
    }
}
