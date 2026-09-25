using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UGREENRemoteDrive;

internal sealed class DriveSettings
{
    public string RemoteUrl { get; set; } = "";
    public string SmbPath { get; set; } = "";
    public string DriveLetter { get; set; } = "U:";
    public bool AutoStart { get; set; }
    public string EncryptedToken { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string Token { get; set; } = "";
}

internal static class SettingsStore
{
    public static readonly string AppDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UGREEN Remote Drive");
    private static readonly string SettingsPath = Path.Combine(AppDirectory, "config.json");

    public static DriveSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new DriveSettings();
            var settings = JsonSerializer.Deserialize<DriveSettings>(File.ReadAllText(SettingsPath)) ?? new DriveSettings();
            if (!string.IsNullOrWhiteSpace(settings.EncryptedToken))
            {
                try
                {
                    var protectedBytes = Convert.FromBase64String(settings.EncryptedToken);
                    settings.Token = Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser));
                }
                catch (CryptographicException)
                {
                    settings.Token = "";
                }
            }
            return settings;
        }
        catch
        {
            return new DriveSettings();
        }
    }

    public static void Save(DriveSettings settings)
    {
        Directory.CreateDirectory(AppDirectory);
        var copy = new DriveSettings
        {
            RemoteUrl = settings.RemoteUrl.Trim(),
            SmbPath = settings.SmbPath.Trim(),
            DriveLetter = settings.DriveLetter.Trim().ToUpperInvariant(),
            AutoStart = settings.AutoStart,
            EncryptedToken = Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(settings.Token), null, DataProtectionScope.CurrentUser))
        };
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, SettingsPath, true);
    }

    public static void Clear()
    {
        if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
    }
}

internal static class DriveLog
{
    private static readonly object Gate = new();
    private static readonly string LogDirectory = Path.Combine(SettingsStore.AppDirectory, "logs");
    private static readonly string LogPath = Path.Combine(LogDirectory, "drive.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            lock (Gate)
            {
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {level} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
