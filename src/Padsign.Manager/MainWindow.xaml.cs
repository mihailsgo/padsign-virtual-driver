using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace Padsign.Manager;

public partial class MainWindow : Window
{
    private readonly AppPaths _paths;
    private readonly DispatcherTimer _refreshTimer;
    private readonly FileSystemWatcher? _configWatcher;
    private Process? _listenerProcess;
    private ManagerConfig _lastSavedConfig = new();
    private bool _suppressFormEvents;
    private bool _syncingAuthFields;
    private bool _isDirty;
    private bool _testUploadSucceeded;
    private ManagerConfig? _lastSuccessfulTestConfig;
    private DateTime? _lastSuccessfulTestAt;
    private bool _testUploadStale;
    private bool _testUploadAttempted;
    private bool _configChangedOnDisk;

    public MainWindow()
    {
        InitializeComponent();
        _paths = AppPaths.Discover();
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += (_, _) =>
        {
            _ = RefreshLiveStateAsync();
            if (MonitoringTab.IsSelected)
                RefreshLogTail();
        };

        EnsureUserConfigExists();
        _configWatcher = CreateConfigWatcher();

        RootPathTextBlock.Text = $"Root: {_paths.RootDirectory}";
        ConfigPathTextBlock.Text = $"Config: {_paths.ConfigPath}";
        ListenerPathTextBlock.Text = $"Listener: {_paths.ListenerExePath}";
        LogPathTextBlock.Text = $"Log: {_paths.LogPath}";
        FooterYearRun.Text = $", {DateTime.Now:yyyy}";

        SetOperationResult("None", true, "No operation executed yet.");
        LoadConfigIntoForm();
        _ = RefreshLiveStateAsync();
        _refreshTimer.Start();
    }

    private FileSystemWatcher? CreateConfigWatcher()
    {
        try
        {
            var configDir = Path.GetDirectoryName(_paths.ConfigPath);
            if (string.IsNullOrWhiteSpace(configDir) || !Directory.Exists(configDir))
                return null;

            var watcher = new FileSystemWatcher(configDir, Path.GetFileName(_paths.ConfigPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            watcher.Changed += (_, _) => OnConfigChangedOnDisk();
            watcher.Created += (_, _) => OnConfigChangedOnDisk();
            watcher.Renamed += (_, _) => OnConfigChangedOnDisk();
            watcher.Deleted += (_, _) => OnConfigChangedOnDisk();
            return watcher;
        }
        catch
        {
            return null;
        }
    }

    private void OnConfigChangedOnDisk()
    {
        Dispatcher.InvokeAsync(async () =>
        {
            _configChangedOnDisk = true;

            if (!_isDirty)
            {
                LoadConfigIntoForm(preserveSuccessfulTestState: true);
                SetStatus("Configuration reloaded after external change.");
                SetOperationResult("Config Sync", true, "Configuration updated from file change.");
                _configChangedOnDisk = false;
            }
            else
            {
                SetStatus("Configuration changed on disk. Save or reload to resolve.", true);
                SetOperationResult("Config Sync", false, "External config change detected while form has unsaved values.");
            }

            await RefreshLiveStateAsync();
        });
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = isError ? Brushes.DarkRed : Brushes.DarkSlateGray;
    }

    private void SetOperationResult(string title, bool success, string message)
    {
        OperationResultTitleTextBlock.Text = $"Last Operation: {title}";
        OperationResultTimeTextBlock.Text = $"Updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        OperationResultMessageTextBlock.Text = message;

        if (success)
        {
            OperationResultCard.Background = new SolidColorBrush(Color.FromRgb(234, 246, 238));
            OperationResultCard.BorderBrush = new SolidColorBrush(Color.FromRgb(181, 223, 195));
        }
        else
        {
            OperationResultCard.Background = new SolidColorBrush(Color.FromRgb(255, 241, 240));
            OperationResultCard.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 206, 204));
        }
    }

    private void LoadConfigIntoForm(bool preserveSuccessfulTestState = false)
    {
        _suppressFormEvents = true;
        try
        {
            EnsureUserConfigExists();
            var cfg = ManagerConfig.Load(_paths.ConfigPath);
            var previousSuccessfulConfig = _lastSuccessfulTestConfig;
            var previousSuccessfulAt = _lastSuccessfulTestAt;
            var previousSucceeded = _testUploadSucceeded;

            ApiUrlTextBox.Text = cfg.ApiUrl;
            AuthHeaderNameTextBox.Text = cfg.AuthenticationHeaderName;
            SetAuthHeaderValue(cfg.AuthenticationHeaderValue);
            EmailTextBox.Text = cfg.Email;
            CompanyTextBox.Text = cfg.Company;
            PortTextBox.Text = cfg.Port.ToString();
            WorkingDirectoryTextBox.Text = cfg.WorkingDirectory;
            UploadTimeoutTextBox.Text = cfg.UploadTimeoutSeconds.ToString();
            MaxRetriesTextBox.Text = cfg.MaxUploadRetries.ToString();
            RetryBackoffTextBox.Text = cfg.RetryBackoffSeconds.ToString();
            CleanupOnSuccessCheckBox.IsChecked = cfg.CleanupOnSuccess;
            _lastSavedConfig = CloneConfig(cfg);
            _isDirty = false;
            if (preserveSuccessfulTestState
                && previousSucceeded
                && previousSuccessfulConfig != null
                && AreConfigsEquivalent(cfg, previousSuccessfulConfig))
            {
                _testUploadSucceeded = true;
                _lastSuccessfulTestConfig = CloneConfig(previousSuccessfulConfig);
                _lastSuccessfulTestAt = previousSuccessfulAt;
                _testUploadStale = false;
                _testUploadAttempted = true;
            }
            else
            {
                _testUploadSucceeded = false;
                _lastSuccessfulTestConfig = null;
                _lastSuccessfulTestAt = null;
                _testUploadStale = false;
                _testUploadAttempted = false;
            }
            ClearValidationErrors();
        }
        finally
        {
            _suppressFormEvents = false;
        }

        UpdateTestUploadStateText();
        UpdateDirtyState();
    }

    private void EnsureUserConfigExists()
    {
        var dir = Path.GetDirectoryName(_paths.ConfigPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(_paths.ConfigPath))
            return;

        try
        {
            if (!File.Exists(_paths.LegacyInstallConfigPath))
                return;

            File.Copy(_paths.LegacyInstallConfigPath, _paths.ConfigPath, overwrite: false);
        }
        catch
        {
            // If migration fails, app continues with default values.
        }
    }

    private void SetAuthHeaderValue(string value)
    {
        _syncingAuthFields = true;
        try
        {
            AuthHeaderValuePasswordBox.Password = value ?? string.Empty;
            AuthHeaderValueVisibleTextBox.Text = value ?? string.Empty;
        }
        finally
        {
            _syncingAuthFields = false;
        }
    }

    private string GetAuthHeaderValue() =>
        ShowAuthHeaderValueCheckBox.IsChecked == true
            ? AuthHeaderValueVisibleTextBox.Text.Trim()
            : AuthHeaderValuePasswordBox.Password.Trim();

    private ManagerConfig BuildConfigFromForm()
    {
        return new ManagerConfig
        {
            ApiUrl = ApiUrlTextBox.Text.Trim(),
            AuthenticationHeaderName = string.IsNullOrWhiteSpace(AuthHeaderNameTextBox.Text) ? "Authorization" : AuthHeaderNameTextBox.Text.Trim(),
            AuthenticationHeaderValue = GetAuthHeaderValue(),
            Email = EmailTextBox.Text.Trim(),
            Company = CompanyTextBox.Text.Trim(),
            Port = ParseInt(PortTextBox.Text, 9100),
            WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectoryTextBox.Text) ? "spool" : WorkingDirectoryTextBox.Text.Trim(),
            UploadTimeoutSeconds = ParseInt(UploadTimeoutTextBox.Text, 30),
            MaxUploadRetries = ParseInt(MaxRetriesTextBox.Text, 3),
            RetryBackoffSeconds = ParseInt(RetryBackoffTextBox.Text, 2),
            CleanupOnSuccess = CleanupOnSuccessCheckBox.IsChecked == true
        };
    }

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text.Trim(), out var value) ? value : fallback;

    private static ManagerConfig CloneConfig(ManagerConfig cfg) =>
        new()
        {
            ApiUrl = cfg.ApiUrl,
            AuthenticationHeaderName = cfg.AuthenticationHeaderName,
            AuthenticationHeaderValue = cfg.AuthenticationHeaderValue,
            ApiKey = cfg.ApiKey,
            Email = cfg.Email,
            Company = cfg.Company,
            Port = cfg.Port,
            WorkingDirectory = cfg.WorkingDirectory,
            UploadTimeoutSeconds = cfg.UploadTimeoutSeconds,
            MaxUploadRetries = cfg.MaxUploadRetries,
            RetryBackoffSeconds = cfg.RetryBackoffSeconds,
            CleanupOnSuccess = cfg.CleanupOnSuccess
        };

    private static bool AreConfigsEquivalent(ManagerConfig a, ManagerConfig b)
    {
        return string.Equals(a.ApiUrl, b.ApiUrl, StringComparison.Ordinal)
               && string.Equals(a.AuthenticationHeaderName, b.AuthenticationHeaderName, StringComparison.Ordinal)
               && string.Equals(a.AuthenticationHeaderValue, b.AuthenticationHeaderValue, StringComparison.Ordinal)
               && string.Equals(a.Email, b.Email, StringComparison.Ordinal)
               && string.Equals(a.Company, b.Company, StringComparison.Ordinal)
               && a.Port == b.Port
               && string.Equals(a.WorkingDirectory, b.WorkingDirectory, StringComparison.Ordinal)
               && a.UploadTimeoutSeconds == b.UploadTimeoutSeconds
               && a.MaxUploadRetries == b.MaxUploadRetries
               && a.RetryBackoffSeconds == b.RetryBackoffSeconds
               && a.CleanupOnSuccess == b.CleanupOnSuccess;
    }

    private void UpdateDirtyState()
    {
        var current = BuildConfigFromForm();
        _isDirty = !AreConfigsEquivalent(current, _lastSavedConfig);
        ConfigSaveStateTextBlock.Text = _isDirty ? "Unsaved changes" : "Saved";
        ConfigSaveStateTextBlock.Foreground = _isDirty ? Brushes.SaddleBrown : Brushes.DarkGreen;
    }

    private void UpdateTestUploadStateText()
    {
        if (_testUploadSucceeded && _lastSuccessfulTestAt.HasValue)
        {
            LastTestUploadStateTextBlock.Text = $"Upload test: Passed at {_lastSuccessfulTestAt.Value:yyyy-MM-dd HH:mm:ss}";
            LastTestUploadStateTextBlock.Foreground = Brushes.DarkGreen;
            return;
        }

        if (_testUploadStale)
        {
            LastTestUploadStateTextBlock.Text = "Upload test: Re-test required (settings changed)";
            LastTestUploadStateTextBlock.Foreground = Brushes.SaddleBrown;
            return;
        }

        if (_testUploadAttempted)
        {
            LastTestUploadStateTextBlock.Text = "Upload test: Last attempt failed";
            LastTestUploadStateTextBlock.Foreground = Brushes.DarkRed;
            return;
        }

        LastTestUploadStateTextBlock.Text = "Upload test: Not run";
        LastTestUploadStateTextBlock.Foreground = Brushes.SaddleBrown;
    }

    private Dictionary<string, string> GetValidationErrors(ManagerConfig cfg)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!Uri.TryCreate(cfg.ApiUrl, UriKind.Absolute, out _))
            errors["ApiUrl"] = "Enter a valid absolute URL.";
        if (string.IsNullOrWhiteSpace(cfg.AuthenticationHeaderName))
            errors["AuthenticationHeaderName"] = "Header name is required.";
        if (string.IsNullOrWhiteSpace(cfg.AuthenticationHeaderValue))
            errors["AuthenticationHeaderValue"] = "Header value is required.";
        if (!ManagerConfig.IsValidEmail(cfg.Email))
            errors["Email"] = "Enter a valid email address.";
        if (string.IsNullOrWhiteSpace(cfg.Company))
            errors["Company"] = "Company is required.";
        if (cfg.Port is <= 0 or > 65535)
            errors["Port"] = "Port must be between 1 and 65535.";
        if (cfg.UploadTimeoutSeconds <= 0)
            errors["UploadTimeoutSeconds"] = "Timeout must be greater than 0.";
        if (cfg.MaxUploadRetries <= 0)
            errors["MaxUploadRetries"] = "Retries must be greater than 0.";
        if (cfg.RetryBackoffSeconds <= 0)
            errors["RetryBackoffSeconds"] = "Backoff must be greater than 0.";

        return errors;
    }

    private void ClearValidationErrors()
    {
        var targets = new[]
        {
            ApiUrlErrorTextBlock,
            AuthHeaderNameErrorTextBlock,
            AuthHeaderValueErrorTextBlock,
            EmailErrorTextBlock,
            CompanyErrorTextBlock,
            PortErrorTextBlock,
            UploadTimeoutErrorTextBlock,
            MaxRetriesErrorTextBlock,
            RetryBackoffErrorTextBlock
        };
        foreach (var target in targets)
        {
            target.Text = string.Empty;
            target.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyValidationErrors(Dictionary<string, string> errors)
    {
        ClearValidationErrors();
        SetValidationError(ApiUrlErrorTextBlock, errors.TryGetValue("ApiUrl", out var apiErr) ? apiErr : null);
        SetValidationError(AuthHeaderNameErrorTextBlock, errors.TryGetValue("AuthenticationHeaderName", out var headerNameErr) ? headerNameErr : null);
        SetValidationError(AuthHeaderValueErrorTextBlock, errors.TryGetValue("AuthenticationHeaderValue", out var headerErr) ? headerErr : null);
        SetValidationError(EmailErrorTextBlock, errors.TryGetValue("Email", out var emailErr) ? emailErr : null);
        SetValidationError(CompanyErrorTextBlock, errors.TryGetValue("Company", out var companyErr) ? companyErr : null);
        SetValidationError(PortErrorTextBlock, errors.TryGetValue("Port", out var portErr) ? portErr : null);
        SetValidationError(UploadTimeoutErrorTextBlock, errors.TryGetValue("UploadTimeoutSeconds", out var timeoutErr) ? timeoutErr : null);
        SetValidationError(MaxRetriesErrorTextBlock, errors.TryGetValue("MaxUploadRetries", out var retriesErr) ? retriesErr : null);
        SetValidationError(RetryBackoffErrorTextBlock, errors.TryGetValue("RetryBackoffSeconds", out var backoffErr) ? backoffErr : null);
    }

    private static void SetValidationError(TextBlock target, string? message)
    {
        target.Text = message ?? string.Empty;
        target.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task RefreshLiveStateAsync()
    {
        try
        {
            UpdateDirtyState();
            var cfg = BuildConfigFromForm();
            var errors = GetValidationErrors(cfg);
            var configValid = errors.Count == 0;
            var configSaved = !_isDirty;
            var listenerRunning = IsListenerRunning();
            var printerInstalled = await IsPrinterInstalledAsync();

            if (_configChangedOnDisk && _isDirty)
            {
                ConfigStatusChipText.Text = "Config: External change";
                ConfigStatusChipText.Foreground = Brushes.DarkRed;
            }
            else if (!configValid)
            {
                ConfigStatusChipText.Text = $"Config: {errors.Count} issue(s)";
                ConfigStatusChipText.Foreground = Brushes.DarkRed;
            }
            else if (!configSaved)
            {
                ConfigStatusChipText.Text = "Config: Valid (Unsaved)";
                ConfigStatusChipText.Foreground = Brushes.SaddleBrown;
            }
            else
            {
                ConfigStatusChipText.Text = "Config: Valid (Saved)";
                ConfigStatusChipText.Foreground = Brushes.DarkGreen;
            }

            ListenerStatusChipText.Text = listenerRunning ? "Listener: Running" : "Listener: Stopped";
            ListenerStatusChipText.Foreground = listenerRunning ? Brushes.DarkGreen : Brushes.SaddleBrown;

            PrinterStatusChipText.Text = printerInstalled ? "Printer: Installed" : "Printer: Missing";
            PrinterStatusChipText.Foreground = printerInstalled ? Brushes.DarkBlue : Brushes.SaddleBrown;

            UpdateReadinessChecklist(configValid, configSaved, printerInstalled, listenerRunning, _testUploadSucceeded);
            UpdateActionAvailability(configValid, configSaved, printerInstalled, listenerRunning);
        }
        catch (Exception ex)
        {
            ConfigStatusChipText.Text = "Config: Status error";
            ConfigStatusChipText.Foreground = Brushes.DarkRed;
            ListenerStatusChipText.Text = "Listener: Status error";
            ListenerStatusChipText.Foreground = Brushes.DarkRed;
            PrinterStatusChipText.Text = "Printer: Status error";
            PrinterStatusChipText.Foreground = Brushes.DarkRed;
            CommandOutputTextBox.Text = ex.ToString();
            SetStatus("Status refresh failed. See command output.", true);
            SetOperationResult("Refresh Status", false, "Automatic status refresh failed.");
        }
    }

    private void UpdateReadinessChecklist(bool configValid, bool configSaved, bool printerInstalled, bool listenerRunning, bool testUploadOk)
    {
        ReadyConfigTextBlock.Text = $"{Mark(configValid && configSaved)} Configure and save valid settings";
        ReadyPrinterTextBlock.Text = $"{Mark(printerInstalled)} Install virtual printer";
        ReadyListenerTextBlock.Text = $"{Mark(listenerRunning)} Start listener process";
        ReadyTestUploadTextBlock.Text = $"{Mark(testUploadOk)} Run successful API upload test";

        var ready = configValid && configSaved && printerInstalled && listenerRunning && testUploadOk;
        if (ready)
        {
            ReadyOverallTextBlock.Text = "System is ready for client printing.";
            ReadyOverallTextBlock.Foreground = Brushes.DarkGreen;
        }
        else
        {
            ReadyOverallTextBlock.Text = "System is not fully ready. Complete pending checklist items.";
            ReadyOverallTextBlock.Foreground = Brushes.SaddleBrown;
        }
    }

    private static string Mark(bool ok) => ok ? "[OK]" : "[TODO]";

    private void UpdateActionAvailability(bool configValid, bool configSaved, bool printerInstalled, bool listenerRunning)
    {
        SaveAndTestButton.IsEnabled = configValid;
        RemovePdfButton.IsEnabled = configValid && configSaved;
        InstallPrinterButton.IsEnabled = configValid && configSaved && !printerInstalled;
        RemovePrinterButton.IsEnabled = printerInstalled;
        if (listenerRunning)
        {
            ToggleListenerButton.Content = "Stop Listener";
            ToggleListenerButton.IsEnabled = true;
        }
        else
        {
            ToggleListenerButton.Content = "Start Listener";
            ToggleListenerButton.IsEnabled = configValid && configSaved && printerInstalled;
        }
    }

    private bool IsListenerRunning()
    {
        if (_listenerProcess is { HasExited: false })
            return true;
        return Process.GetProcessesByName("Padsign.Listener").Any();
    }

    private async Task<bool> IsPrinterInstalledAsync()
    {
        var result = await RunProcessAsync("powershell", "-NoProfile -ExecutionPolicy Bypass -Command \"Get-Printer -Name 'Padsign' -ErrorAction SilentlyContinue | Select-Object -First 1\"");
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput);
    }

    private void FormFieldChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressFormEvents || _syncingAuthFields)
            return;

        UpdateDirtyState();
        var current = BuildConfigFromForm();
        if (_lastSuccessfulTestConfig == null || !AreConfigsEquivalent(current, _lastSuccessfulTestConfig))
        {
            if (_testUploadSucceeded)
                _testUploadStale = true;
            _testUploadSucceeded = false;
        }

        UpdateTestUploadStateText();
        _ = RefreshLiveStateAsync();
    }

    private void ShowAuthHeaderValueCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        AuthHeaderValueVisibleTextBox.Visibility = Visibility.Visible;
        AuthHeaderValuePasswordBox.Visibility = Visibility.Collapsed;
        AuthHeaderValueVisibleTextBox.Text = AuthHeaderValuePasswordBox.Password;
    }

    private void ShowAuthHeaderValueCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        AuthHeaderValuePasswordBox.Visibility = Visibility.Visible;
        AuthHeaderValueVisibleTextBox.Visibility = Visibility.Collapsed;
        AuthHeaderValuePasswordBox.Password = AuthHeaderValueVisibleTextBox.Text;
    }

    private void AuthHeaderValuePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingAuthFields)
            return;

        _syncingAuthFields = true;
        try
        {
            AuthHeaderValueVisibleTextBox.Text = AuthHeaderValuePasswordBox.Password;
        }
        finally
        {
            _syncingAuthFields = false;
        }

        FormFieldChanged(sender, e);
    }

    private void AuthHeaderValueVisibleTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingAuthFields)
            return;

        _syncingAuthFields = true;
        try
        {
            AuthHeaderValuePasswordBox.Password = AuthHeaderValueVisibleTextBox.Text;
        }
        finally
        {
            _syncingAuthFields = false;
        }

        FormFieldChanged(sender, e);
    }

    private void CopyAuthHeaderValueButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(GetAuthHeaderValue());
            SetStatus("Authentication header copied to clipboard.");
            SetOperationResult("Copy Auth Header", true, "Authentication header value copied.");
        }
        catch (Exception ex)
        {
            CommandOutputTextBox.Text = ex.ToString();
            SetStatus("Failed to copy authentication header.", true);
            SetOperationResult("Copy Auth Header", false, "Copy operation failed. See command output.");
        }
    }

    private async void SaveAndTestButton_Click(object sender, RoutedEventArgs e)
    {
        var cfg = BuildConfigFromForm();
        var errors = GetValidationErrors(cfg);
        ApplyValidationErrors(errors);
        if (errors.Count > 0)
        {
            SetStatus("Cannot save and test. Fix validation issues first.", true);
            CommandOutputTextBox.Text = string.Join(Environment.NewLine, errors.Values.Select((x, i) => $"{i + 1}. {x}"));
            SetOperationResult("Save Config And Test", false, "Action blocked by validation errors.");
            return;
        }

        try
        {
            cfg.Save(_paths.ConfigPath);
            _lastSavedConfig = CloneConfig(cfg);
            _isDirty = false;
            SetStatus("Configuration saved. Running test upload...");
            CommandOutputTextBox.Text = $"Saved: {_paths.ConfigPath}{Environment.NewLine}";
        }
        catch (Exception ex)
        {
            CommandOutputTextBox.Text = ex.ToString();
            SetStatus("Save failed.", true);
            SetOperationResult("Save Config And Test", false, "Configuration save failed. See command output.");
            await RefreshLiveStateAsync();
            return;
        }

        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(cfg.UploadTimeoutSeconds)
            };
            SetRequestHeader(client, cfg.AuthenticationHeaderName, cfg.AuthenticationHeaderValue);

            var testPdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF");
            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(testPdf);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            content.Add(fileContent, "file", $"padsign-test-{DateTime.Now:yyyyMMddHHmmss}.pdf");
            content.Add(new StringContent(cfg.Email), "email");
            content.Add(new StringContent(cfg.Company), "company");

            var response = await client.PostAsync(cfg.ApiUrl, content);
            var body = await response.Content.ReadAsStringAsync();
            CommandOutputTextBox.Text = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}{Environment.NewLine}{TrimBody(body)}";
            _testUploadAttempted = true;
            _testUploadSucceeded = response.IsSuccessStatusCode;

            if (response.IsSuccessStatusCode)
            {
                _lastSuccessfulTestConfig = CloneConfig(cfg);
                _lastSuccessfulTestAt = DateTime.Now;
                _testUploadStale = false;
                SetStatus("Configuration saved and test upload succeeded.");
                SetOperationResult("Save Config And Test", true, "Configuration saved and registerPDF test upload succeeded.");
            }
            else
            {
                _lastSuccessfulTestConfig = null;
                _lastSuccessfulTestAt = null;
                _testUploadStale = false;
                SetStatus("Configuration saved, but test upload failed. See command output.", true);
                var friendly = BuildFriendlyUploadError(response.StatusCode, response.ReasonPhrase, body);
                SetOperationResult("Save Config And Test", false, friendly);
            }
        }
        catch (Exception ex)
        {
            _testUploadAttempted = true;
            _testUploadSucceeded = false;
            _lastSuccessfulTestConfig = null;
            _lastSuccessfulTestAt = null;
            _testUploadStale = false;
            CommandOutputTextBox.Text = ex.ToString();
            SetStatus("Configuration saved, but test upload failed with exception.", true);
            var friendly = BuildFriendlyUploadException(ex);
            SetOperationResult("Save Config And Test", false, friendly);
        }

        UpdateTestUploadStateText();
        await RefreshLiveStateAsync();
    }

    private async void RemovePdfButton_Click(object sender, RoutedEventArgs e)
    {
        var cfg = BuildConfigFromForm();
        var errors = GetValidationErrors(cfg);
        ApplyValidationErrors(errors);
        if (errors.Count > 0)
        {
            SetStatus("Cannot remove PDF. Fix validation issues first.", true);
            CommandOutputTextBox.Text = string.Join(Environment.NewLine, errors.Values.Select((x, i) => $"{i + 1}. {x}"));
            SetOperationResult("Remove PDF", false, "Remove blocked by validation errors.");
            return;
        }

        if (_isDirty)
        {
            SetStatus("Save configuration before removing PDF.", true);
            SetOperationResult("Remove PDF", false, "Save configuration first.");
            return;
        }

        try
        {
            var removeUri = BuildRemoveUserUri(cfg.ApiUrl, cfg.Email, cfg.Company);
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(cfg.UploadTimeoutSeconds)
            };
            SetRequestHeader(client, cfg.AuthenticationHeaderName, cfg.AuthenticationHeaderValue);

            var response = await client.GetAsync(removeUri);
            var body = await response.Content.ReadAsStringAsync();
            CommandOutputTextBox.Text = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}{Environment.NewLine}{TrimBody(body)}";

            if (response.IsSuccessStatusCode)
            {
                SetStatus("Remove PDF request succeeded.");
                SetOperationResult("Remove PDF", true, $"Session removed for {cfg.Email}|{cfg.Company}.");
            }
            else
            {
                SetStatus("Remove PDF request failed. See command output.", true);
                SetOperationResult("Remove PDF", false,
                    $"removeUser failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Body: {TrimBody(body)}");
            }
        }
        catch (Exception ex)
        {
            CommandOutputTextBox.Text = ex.ToString();
            SetStatus("Remove PDF request failed with exception.", true);
            SetOperationResult("Remove PDF", false, $"Remove PDF failed: {FormatTechnicalError(ex)}");
        }

        await RefreshLiveStateAsync();
    }

    private async void InstallPrinterButton_Click(object sender, RoutedEventArgs e)
    {
        var cfg = BuildConfigFromForm();
        var errors = GetValidationErrors(cfg);
        ApplyValidationErrors(errors);
        if (errors.Count > 0 || _isDirty)
        {
            SetStatus("Save a valid configuration before installing printer.", true);
            SetOperationResult("Install Printer", false, "Install blocked until configuration is valid and saved.");
            return;
        }

        var script = _paths.InstallPrinterScriptPath;
        if (!File.Exists(script))
        {
            SetStatus("Install script not found.", true);
            SetOperationResult("Install Printer", false, "Install script not found.");
            return;
        }

        SetStatus("Installing printer. UAC prompt may appear.");
        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -PrinterName \"Padsign\" -PortNumber {cfg.Port}";
        var result = await RunProcessAsync("powershell", args);
        CommandOutputTextBox.Text = BuildProcessOutput(result);

        var success = result.ExitCode == 0;
        SetStatus(success ? "Printer installation command finished." : "Printer installation command failed.", !success);
        SetOperationResult("Install Printer", success, success
            ? "Printer setup finished."
            : "Printer setup failed. Review command output.");
        await RefreshLiveStateAsync();
    }

    private async void RemovePrinterButton_Click(object sender, RoutedEventArgs e)
    {
        var script = _paths.RemovePrinterScriptPath;
        if (!File.Exists(script))
        {
            SetStatus("Remove script not found.", true);
            SetOperationResult("Remove Printer", false, "Remove script not found.");
            return;
        }

        var confirm = MessageBox.Show(
            "This will remove the Padsign printer from this machine. Continue?",
            "Confirm Remove Printer",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        SetStatus("Removing printer. UAC prompt may appear.");
        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -PrinterName \"Padsign\" -PortName \"PADPORT\"";
        var result = await RunProcessAsync("powershell", args);
        CommandOutputTextBox.Text = BuildProcessOutput(result);

        var success = result.ExitCode == 0;
        SetStatus(success ? "Printer removal command finished." : "Printer removal command failed.", !success);
        SetOperationResult("Remove Printer", success, success
            ? "Printer removal finished."
            : "Printer removal failed. Review command output.");
        await RefreshLiveStateAsync();
    }

    private async void ToggleListenerButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsListenerRunning())
            await StopListenerAsync();
        else
            await StartListenerAsync();
    }

    private async Task StartListenerAsync()
    {
        var cfg = BuildConfigFromForm();
        var errors = GetValidationErrors(cfg);
        ApplyValidationErrors(errors);
        if (errors.Count > 0 || _isDirty)
        {
            SetStatus("Save a valid configuration before starting listener.", true);
            SetOperationResult("Start Listener", false, "Start blocked until configuration is valid and saved.");
            return;
        }

        var printerInstalled = await IsPrinterInstalledAsync();
        if (!printerInstalled)
        {
            SetStatus("Install printer before starting listener.", true);
            SetOperationResult("Start Listener", false, "Printer is not installed.");
            return;
        }

        if (!_testUploadSucceeded)
        {
            var proceed = MessageBox.Show(
                "Test upload has not succeeded yet. Start listener anyway?",
                "Start Without Upload Test",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (proceed != MessageBoxResult.Yes)
                return;
        }

        try
        {
            if (!File.Exists(_paths.ListenerExePath))
            {
                SetStatus("Listener executable not found. Publish listener first.", true);
                SetOperationResult("Start Listener", false, "Listener executable not found.");
                return;
            }

            cfg.Save(_paths.ConfigPath);
            cfg.Save(_paths.ListenerConfigPath);

            if (_listenerProcess is { HasExited: false })
            {
                SetStatus("Listener already started by manager.");
                SetOperationResult("Start Listener", true, "Listener was already running.");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = _paths.ListenerExePath,
                Arguments = $"\"{_paths.ListenerConfigPath}\"",
                WorkingDirectory = Path.GetDirectoryName(_paths.ListenerExePath) ?? _paths.RootDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            _listenerProcess = Process.Start(psi);
            SetStatus("Listener started.");
            SetOperationResult("Start Listener", true, "Listener process started.");
        }
        catch (Exception ex)
        {
            CommandOutputTextBox.Text = ex.ToString();
            SetStatus("Failed to start listener.", true);
            SetOperationResult("Start Listener", false, "Failed to start listener. See command output.");
        }

        await RefreshLiveStateAsync();
    }

    private async Task StopListenerAsync()
    {
        var runningProcesses = Process.GetProcessesByName("Padsign.Listener");
        if (runningProcesses.Length == 0 && (_listenerProcess == null || _listenerProcess.HasExited))
        {
            SetStatus("Listener is not running.");
            SetOperationResult("Stop Listener", true, "No running listener process found.");
            return;
        }

        var confirm = MessageBox.Show(
            $"This will stop {runningProcesses.Length} listener process(es). Continue?",
            "Confirm Stop Listener",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            if (_listenerProcess is { HasExited: false })
            {
                _listenerProcess.Kill(entireProcessTree: true);
                _listenerProcess.Dispose();
                _listenerProcess = null;
            }

            foreach (var proc in runningProcesses)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore per-process failures
                }
                finally
                {
                    proc.Dispose();
                }
            }

            SetStatus("Listener stop command executed.");
            SetOperationResult("Stop Listener", true, "Listener processes were stopped.");
        }
        catch (Exception ex)
        {
            CommandOutputTextBox.Text = ex.ToString();
            SetStatus("Failed to stop listener.", true);
            SetOperationResult("Stop Listener", false, "Failed to stop listener. See command output.");
        }

        await RefreshLiveStateAsync();
    }

    private void RefreshLogButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshLogTail();
        SetStatus("Log refreshed.");
        SetOperationResult("Refresh Log", true, "Log tail refreshed.");
    }

    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(_paths.LogPath))
            {
                SetStatus("No log file found to copy.", true);
                SetOperationResult("Copy Log", false, "No log file found.");
                return;
            }

            var logText = File.ReadAllText(_paths.LogPath);
            Clipboard.SetText(logText);
            SetStatus("Log file copied to clipboard.");
            SetOperationResult("Copy Log", true, "Log file content copied.");
            CommandOutputTextBox.Text = $"Copied log file: {_paths.LogPath}{Environment.NewLine}Characters: {logText.Length}";
        }
        catch (Exception ex)
        {
            CommandOutputTextBox.Text = ex.ToString();
            SetStatus("Failed to copy log file.", true);
            SetOperationResult("Copy Log", false, "Copy log failed. See command output.");
        }
    }

    private void ClearOutputButton_Click(object sender, RoutedEventArgs e)
    {
        LogTailTextBox.Clear();
        CommandOutputTextBox.Clear();
        SetStatus("Output cleared.");
        SetOperationResult("Clear Output", true, "Monitoring and command output cleared.");
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        var helpWindow = new Window
        {
            Title = "Padsign Help",
            Owner = this,
            Width = 920,
            Height = 700,
            MinWidth = 760,
            MinHeight = 540,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(243, 245, 248)),
            Content = new Border
            {
                Margin = new Thickness(14),
                Padding = new Thickness(14),
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(224, 230, 237)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = new TextBox
                {
                    Text = BuildHelpText(),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    IsReadOnly = true,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.White
                }
            }
        };
        helpWindow.ShowDialog();
    }

    private void RefreshLogTail()
    {
        if (!File.Exists(_paths.LogPath))
        {
            LogTailTextBox.Text = "No log file found yet.";
            return;
        }

        var lines = File.ReadAllLines(_paths.LogPath);
        var tail = lines.TakeLast(200);
        LogTailTextBox.Text = string.Join(Environment.NewLine, tail);
        LogTailTextBox.ScrollToEnd();
    }

    private static void SetRequestHeader(HttpClient client, string headerName, string headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerName) || string.IsNullOrWhiteSpace(headerValue))
            return;

        if (headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
        {
            if (!AuthenticationHeaderValue.TryParse(headerValue, out var auth))
                throw new InvalidOperationException("Authentication header value is invalid for Authorization.");
            client.DefaultRequestHeaders.Authorization = auth;
            return;
        }

        client.DefaultRequestHeaders.TryAddWithoutValidation(headerName, headerValue);
    }

    private static string TrimBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;
        return body.Length <= 2000 ? body : $"{body[..2000]}...";
    }

    private static string FormatTechnicalError(Exception ex)
    {
        var message = $"{ex.GetType().Name}: {ex.Message}";
        if (ex.InnerException != null)
            message += $" | Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
        return message;
    }

    private static string BuildFriendlyUploadError(System.Net.HttpStatusCode statusCode, string? reasonPhrase, string body)
    {
        var code = (int)statusCode;
        var reason = string.IsNullOrWhiteSpace(reasonPhrase) ? statusCode.ToString() : reasonPhrase;
        var trimmedBody = TrimBody(body);

        if (code == 401 || code == 403)
            return $"Authorization error. Please check access credentials (authentication header name/value). Technical: HTTP {code} {reason}.";
        if (code == 404)
            return $"Could not find API endpoint. Please check API URL. Technical: HTTP {code} {reason}.";
        if (code >= 500)
            return $"Server error on API side. Please try again later or contact administrator. Technical: HTTP {code} {reason}.";
        if (code == 400)
            return $"Request was rejected by API. Please verify email/company and endpoint format. Technical: HTTP {code} {reason}. Body: {trimmedBody}";
        if (code == 429)
            return $"Too many requests. Please wait and try again. Technical: HTTP {code} {reason}.";

        return $"Upload failed. Please verify settings and try again. Technical: HTTP {code} {reason}. Body: {trimmedBody}";
    }

    private static string BuildFriendlyUploadException(Exception ex)
    {
        if (ex is TaskCanceledException)
            return $"Could not connect to server (timeout). Please check network and API URL. Technical: {FormatTechnicalError(ex)}";
        if (ex is HttpRequestException)
            return $"Could not connect to server. Please check network, DNS, proxy/firewall and API URL. Technical: {FormatTechnicalError(ex)}";
        if (ex is InvalidOperationException && ex.Message.Contains("Authentication header", StringComparison.OrdinalIgnoreCase))
            return $"Authorization header format is invalid. Please check access credentials. Technical: {FormatTechnicalError(ex)}";

        return $"Upload failed due to unexpected error. Technical: {FormatTechnicalError(ex)}";
    }

    private static string BuildRemoveUserUri(string apiUrl, string email, string company)
    {
        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var parsed))
            throw new InvalidOperationException("ApiUrl is invalid.");

        var endpointPath = parsed.AbsolutePath;
        if (endpointPath.EndsWith("/registerPDF", StringComparison.OrdinalIgnoreCase))
            endpointPath = endpointPath[..^"/registerPDF".Length] + "/removeUser";
        else
            endpointPath = "/api/removeUser";

        var builder = new UriBuilder(parsed)
        {
            Path = endpointPath,
            Query = $"email={Uri.EscapeDataString(email)}&company={Uri.EscapeDataString(company)}"
        };
        return builder.Uri.ToString();
    }

    private static string BuildProcessOutput(ProcessResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Exit code: {result.ExitCode}");
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            sb.AppendLine("STDOUT:");
            sb.AppendLine(result.StandardOutput);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            sb.AppendLine("STDERR:");
            sb.AppendLine(result.StandardError);
        }
        return sb.ToString();
    }

    private static string BuildHelpText()
    {
        return
@"PADSIGN HELP

1) What this application does
- Installs and manages the local Padsign virtual printer workflow.
- Captures print jobs through local RAW port (default 9100).
- Uploads only PDF print jobs to Padsign register endpoint using configured auth and user session:
  email + company.
- If document is not PDF, request is skipped and logged clearly.

2) Required setup (first run)
- Fill Setup fields:
  ApiUrl, Authentication Header Name, Authentication Header Value, Email, Company
- Click: Save And Test PDF Sending
- Open Operations tab:
  Install Printer (Admin) -> Start Listener (toggle button)

3) Main actions
- Save And Test PDF Sending:
  Saves current settings and immediately sends a small test PDF to the configured API with current email/company.
- Remove PDF:
  Calls /removeUser with current email+company using same auth header.
  No extra input required.
- Start Listener:
  Starts local listener process with saved config.

4) Monitoring
- Monitoring tab auto-refreshes logs while tab is open.
- Command Output panel shows API responses and command diagnostics.

5) Common issues and fixes
- Test upload succeeds in Setup but start flow complains:
  Re-save config and test again; upload success is tied to current saved config.
- 401/403 from API:
  Verify Authentication Header Name/Value and API key validity.
- 400 from registerPDF:
  Verify ApiUrl points to register endpoint and Email/Company are filled.
- removeUser fails:
  ApiUrl should be in /api/registerPDF form so app resolves /api/removeUser.
  Also verify API key has rights for removeUser.
- Listener start fails:
  Ensure listener executable exists and config is saved.
- Printing does nothing:
  Ensure printer is installed and listener is running.
  Check port in config equals printer port.
- Log says ""document is not PDF. Upload request has not been made."":
  Input job format is not PDF. Current behavior is skip-with-log (no API request).
- Build/publish errors about locked files:
  Stop running Padsign.Listener process and retry packaging.

6) Admin notes
- Config is edited in UI but persisted in padsign.json for runtime stability.
- Logs are at listener logs folder and shown in Monitoring tab.
- Installer resets old listener logs to start with fresh logs.

7) Recommended quick diagnostics
- Run Save Config And Test and confirm HTTP 2xx.
- Start listener and print a one-page test document to Padsign.
- Check Monitoring log for:
  format detection, skip-or-upload status, email|company session info.
";
    }

    private void TrustlynxLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
            e.Handled = true;
        }
        catch
        {
            SetStatus("Could not open https://trustlynx.com", true);
            SetOperationResult("Open Link", false, "Failed to open Trustlynx website.");
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi };
        var started = proc.Start();
        if (!started)
            throw new InvalidOperationException($"Failed to start process {fileName}");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcessResult(proc.ExitCode, stdout, stderr);
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        if (_configWatcher != null)
        {
            _configWatcher.EnableRaisingEvents = false;
            _configWatcher.Dispose();
        }
        base.OnClosed(e);
    }

    private readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
