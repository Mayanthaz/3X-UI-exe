using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MaizyVpsSetup;

/// <summary>Remembers the form between runs. Secrets are encrypted with Windows DPAPI (current user only).</summary>
public sealed class AppSettings
{
    public const string DefaultPanelUrl = "https://tunnel.inverses.dev:2020/";

    public string ServerIp { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string SshUser { get; set; } = "root";
    public bool UseKey { get; set; }
    public string KeyPath { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public string PanelUrl { get; set; } = DefaultPanelUrl;
    public string AcmeEmail { get; set; } = "";
    public bool UpdateCloudflare { get; set; }
    public bool RememberSecrets { get; set; }
    public string? PasswordProtected { get; set; }
    public string? CfTokenProtected { get; set; }

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MaizyVpsSetup");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* corrupt settings: start fresh */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* not critical */ }
    }

    public static string? Protect(string value) => string.IsNullOrEmpty(value)
        ? null
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}
