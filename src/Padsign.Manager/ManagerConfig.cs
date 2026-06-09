using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Padsign.Manager;

internal sealed class ManagerConfig
{
    public string ApiUrl { get; set; } = string.Empty;
    public string AuthenticationHeaderName { get; set; } = "Authorization";
    public string AuthenticationHeaderValue { get; set; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKey { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public int Port { get; set; } = 9100;
    public string WorkingDirectory { get; set; } = "spool";
    public int UploadTimeoutSeconds { get; set; } = 30;
    public int MaxUploadRetries { get; set; } = 3;
    public int RetryBackoffSeconds { get; set; } = 2;
    public bool CleanupOnSuccess { get; set; }

    // ── Receive-back (signed PDF delivered back to this desktop) ──
    public string SignedOutputPath { get; set; } = @"D:\VM\SignedDocs";
    public bool ReceiveBackEnabled { get; set; } = true;
    public int ReceiveBackPollSeconds { get; set; } = 5;
    public int ReceiveBackTimeoutMinutes { get; set; } = 30;

    public static ManagerConfig Load(string path)
    {
        if (!File.Exists(path))
            return new ManagerConfig();

        var json = File.ReadAllText(path);
        var model = JsonSerializer.Deserialize<ManagerConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (model == null)
            return new ManagerConfig();

        if (string.IsNullOrWhiteSpace(model.AuthenticationHeaderName))
            model.AuthenticationHeaderName = "Authorization";

        return model;
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(path, json);
    }

    public static bool IsValidEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            _ = new System.Net.Mail.MailAddress(value);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
