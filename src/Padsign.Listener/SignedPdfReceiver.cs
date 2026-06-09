using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Padsign.Listener;

/// <summary>
/// Pulls signed PDFs back from the PadSign server (Manager-initiated polling).
/// Flow: GET /signedPdf/pending -> GET /signedPdf?docid= (save using the
/// X-Padsign-Filename header) -> POST /signedPdf/ack (server deletes its copy).
/// </summary>
internal sealed record PendingItem(string Docid, string DocumentNumber, string SignedAt,
    string Filename, long SizeBytes);

internal sealed class SignedPdfReceiver
{
    private readonly PadsignConfig _config;
    private readonly FileLogger _logger;
    private readonly string _statusPath;
    private readonly string _receivedStatePath;
    private readonly object _stateGate = new();

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public SignedPdfReceiver(PadsignConfig config, FileLogger logger)
    {
        _config = config;
        _logger = logger;
        _statusPath = Path.Combine(_config.WorkingDirectory, "receiveback-status.json");
        _receivedStatePath = Path.Combine(_config.WorkingDirectory, "received-pending-ack.json");
    }

    // ── Public entry points ──────────────────────────────────────────────────

    /// <summary>One-time scan at startup: deliver anything already signed while we were down.</summary>
    public async Task CatchUpAsync(CancellationToken ct)
    {
        if (!_config.ReceiveBackEnabled) return;
        _logger.Info("Receive-back: startup catch-up scan...");
        try
        {
            using var client = CreateClient();
            var pending = await GetPendingAsync(client, ct);
            _logger.Info($"Receive-back: catch-up found {pending.Count} pending document(s).");
            foreach (var item in pending)
                await FetchSaveAckAsync(client, item.Docid, ct);
        }
        catch (Exception ex)
        {
            _logger.Error($"Receive-back: catch-up failed: {ex.Message}");
            WriteStatus("failed", $"catch-up: {ex.Message}", null, null);
        }
    }

    /// <summary>After an upload, poll until our document is signed (or timeout).</summary>
    public async Task PollAndFetchAsync(string? docidHint, string jobId, CancellationToken ct)
    {
        if (!_config.ReceiveBackEnabled) return;
        if (string.IsNullOrWhiteSpace(docidHint))
        {
            // No docId from the upload response — we cannot identify THIS job's
            // document, and draining the pending list would steal documents that
            // other concurrent jobs are still waiting for. Startup catch-up delivers it.
            _logger.Error($"Job {jobId}: no docId from upload; skipping receive-back poll (will be delivered on next startup catch-up).");
            return;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(_config.ReceiveBackTimeoutMinutes);
        var period = TimeSpan.FromSeconds(_config.ReceiveBackPollSeconds);
        _logger.Info($"Job {jobId}: receive-back poll start for doc {docidHint} (every {_config.ReceiveBackPollSeconds}s, timeout {_config.ReceiveBackTimeoutMinutes}m).");

        using var client = CreateClient();
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var pending = await GetPendingAsync(client, ct);
                _logger.Info($"Job {jobId}: receive-back poll — {pending.Count} pending.");
                var target = pending.FirstOrDefault(p => p.Docid == docidHint);
                if (target != null)
                {
                    await FetchSaveAckAsync(client, target.Docid, ct);
                    return; // our document handled
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Job {jobId}: receive-back poll error: {ex.Message}");
                WriteStatus("failed", ex.Message, docidHint, null);
            }

            try { await Task.Delay(period, ct); }
            catch (OperationCanceledException) { return; }
        }

        _logger.Info($"Job {jobId}: receive-back poll stopped for doc {docidHint} (timeout reached).");
        WriteStatus("pending", "timed out waiting for signed document", docidHint, null);
    }

    // ── Core steps ───────────────────────────────────────────────────────────

    private async Task<List<PendingItem>> GetPendingAsync(HttpClient client, CancellationToken ct)
    {
        var url = Endpoint("/signedPdf/pending")
            + $"?email={Uri.EscapeDataString(_config.Email)}&company={Uri.EscapeDataString(_config.Company)}";
        var resp = await client.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"pending HTTP {(int)resp.StatusCode}: {Truncate(body, 300)}");
        return JsonSerializer.Deserialize<List<PendingItem>>(body, JsonOpts) ?? new List<PendingItem>();
    }

    /// <summary>Download + save + ack one document. Returns true if it ended up saved locally.</summary>
    private async Task<bool> FetchSaveAckAsync(HttpClient client, string docid, CancellationToken ct)
    {
        // If we already saved this doc but the ack failed before, skip download — just retry ack.
        var saved = LoadSavedState();
        if (saved.TryGetValue(docid, out var priorPath) && File.Exists(priorPath))
        {
            _logger.Info($"Receive-back: doc {docid} already saved at {priorPath}; retrying ack only.");
            return await TryAckAsync(client, docid, priorPath, ct);
        }

        // 1) download
        string filename;
        byte[] bytes;
        try
        {
            var resp = await client.GetAsync(Endpoint("/signedPdf") + $"?docid={Uri.EscapeDataString(docid)}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                var b = await resp.Content.ReadAsStringAsync(ct);
                _logger.Error($"Receive-back: download {docid} failed HTTP {(int)resp.StatusCode}: {Truncate(b, 300)}");
                WriteStatus("failed", $"download HTTP {(int)resp.StatusCode}", docid, null);
                return false;
            }
            filename = resp.Headers.TryGetValues("X-Padsign-Filename", out var fv)
                ? (fv.FirstOrDefault() ?? $"{docid}.pdf")
                : $"{docid}.pdf";
            bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            _logger.Info($"Receive-back: downloaded {docid} -> {filename} ({bytes.Length} bytes).");
        }
        catch (Exception ex)
        {
            _logger.Error($"Receive-back: download {docid} error (network?): {ex.Message}");
            WriteStatus("failed", $"network: {ex.Message}", docid, null);
            return false;
        }

        // 2) save (folder-not-writable / write error are surfaced explicitly)
        string targetPath;
        try
        {
            Directory.CreateDirectory(_config.SignedOutputPath);
            targetPath = Path.Combine(_config.SignedOutputPath, SanitizeFileName(filename));
            await File.WriteAllBytesAsync(targetPath, bytes, ct);
            _logger.Info($"Receive-back: saved {docid} -> {targetPath}.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Error($"Receive-back: folder not writable {_config.SignedOutputPath}: {ex.Message}");
            WriteStatus("failed", $"folder not writable: {_config.SignedOutputPath}", docid, null);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error($"Receive-back: file write error for {docid}: {ex.Message}");
            WriteStatus("failed", $"file write error: {ex.Message}", docid, null);
            return false;
        }

        // Remember saved-before-ack so a failed ack never causes a re-download.
        SaveSavedState(docid, targetPath);
        return await TryAckAsync(client, docid, targetPath, ct);
    }

    private async Task<bool> TryAckAsync(HttpClient client, string docid, string savedPath, CancellationToken ct)
    {
        try
        {
            _logger.Info($"Receive-back: ack sent for {docid}.");
            var body = new StringContent($"{{\"docid\":{JsonSerializer.Serialize(docid)}}}", Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(Endpoint("/signedPdf/ack"), body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var b = await resp.Content.ReadAsStringAsync(ct);
                _logger.Error($"Receive-back: ack {docid} non-2xx HTTP {(int)resp.StatusCode}: {Truncate(b, 300)} (file kept; will retry).");
                WriteStatus("pending", $"ack HTTP {(int)resp.StatusCode}; will retry", docid, savedPath);
                return true; // saved locally; ack retried next tick / next launch
            }
            _logger.Info($"Receive-back: ack acknowledged for {docid}.");
            RemoveSavedState(docid);
            WriteStatus("success", "delivered", docid, savedPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Receive-back: ack {docid} error: {ex.Message} (file kept; will retry).");
            WriteStatus("pending", $"ack error: {ex.Message}; will retry", docid, savedPath);
            return true;
        }
    }

    // ── HTTP + endpoint helpers ──────────────────────────────────────────────

    private HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(_config.UploadTimeoutSeconds) };
        SetAuth(client, _config.AuthenticationHeaderName, _config.AuthenticationHeaderValue);
        return client;
    }

    private static void SetAuth(HttpClient client, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value)) return;
        if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
        {
            if (AuthenticationHeaderValue.TryParse(value, out var auth))
            {
                client.DefaultRequestHeaders.Authorization = auth;
                return;
            }
            throw new InvalidOperationException("AuthenticationHeaderValue is not a valid Authorization header value.");
        }
        client.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
    }

    // ".../api/registerPDF" -> ".../api/signedPdf[/pending|/ack]"
    private string Endpoint(string suffix)
    {
        if (!Uri.TryCreate(_config.ApiUrl, UriKind.Absolute, out var parsed))
            throw new InvalidOperationException("ApiUrl is invalid.");
        var p = parsed.AbsolutePath;
        var basePath = p.EndsWith("/registerPDF", StringComparison.OrdinalIgnoreCase)
            ? p[..^"/registerPDF".Length]
            : "/api";
        return new UriBuilder(parsed) { Path = basePath + suffix, Query = string.Empty }.Uri.ToString();
    }

    // ── Status file (read by the Manager UI on its refresh timer) ─────────────

    private void WriteStatus(string lastStatus, string reason, string? docid, string? savedPath)
    {
        var payload = new
        {
            lastStatus,
            reason,
            docid,
            filename = savedPath != null ? Path.GetFileName(savedPath) : null,
            savedPath,
            timestampUtc = DateTime.UtcNow.ToString("O")
        };
        lock (_stateGate)
        {
            try
            {
                Directory.CreateDirectory(_config.WorkingDirectory);
                File.WriteAllText(_statusPath, JsonSerializer.Serialize(payload), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger.Error($"Receive-back: failed to write status file: {ex.Message}");
            }
        }
    }

    // ── Local saved-but-not-acked state (docid -> savedPath) ──────────────────

    private Dictionary<string, string> LoadSavedState()
    {
        lock (_stateGate)
        {
            try
            {
                if (!File.Exists(_receivedStatePath)) return new Dictionary<string, string>();
                var json = File.ReadAllText(_receivedStatePath);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }
    }

    private void SaveSavedState(string docid, string savedPath)
    {
        lock (_stateGate)
        {
            var map = LoadSavedStateNoLock();
            map[docid] = savedPath;
            WriteSavedStateNoLock(map);
        }
    }

    private void RemoveSavedState(string docid)
    {
        lock (_stateGate)
        {
            var map = LoadSavedStateNoLock();
            if (map.Remove(docid))
                WriteSavedStateNoLock(map);
        }
    }

    private Dictionary<string, string> LoadSavedStateNoLock()
    {
        try
        {
            if (!File.Exists(_receivedStatePath)) return new Dictionary<string, string>();
            var json = File.ReadAllText(_receivedStatePath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private void WriteSavedStateNoLock(Dictionary<string, string> map)
    {
        try
        {
            Directory.CreateDirectory(_config.WorkingDirectory);
            File.WriteAllText(_receivedStatePath, JsonSerializer.Serialize(map), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.Error($"Receive-back: failed to write saved-state file: {ex.Message}");
        }
    }

    // ── small helpers ─────────────────────────────────────────────────────────

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "signed.pdf";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        return sb.ToString();
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max] + "...";
}
