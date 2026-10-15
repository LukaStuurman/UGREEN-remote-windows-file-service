using System.Text.Json;

namespace UGREENRemoteDrive;

internal sealed record DesktopTileSelection(string Id, string Name)
{
    public static DesktopTileSelection? Validate(string? id, string? name)
    {
        id = id?.Trim() ?? "";
        name = name?.Trim() ?? "";
        if (id.Length > 200 || name.Length is 0 or > 128 ||
            id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-' and not '@' and not ':') ||
            name.Any(char.IsControl)) return null;
        return new(id, name);
    }
}

internal static class DesktopAutomation
{
    internal const int WaitMinutes = 15;
    internal static string BuildScript(DriveSettings settings, Uri? serviceRoot)
    {
        using var stream = typeof(DesktopAutomation).Assembly.GetManifestResourceStream(
            "UGREENRemoteDrive.Browser.DesktopAutomation.js")!;
        using var reader = new StreamReader(stream);
        var config = JsonSerializer.Serialize(new
        {
            expectedOrigin = serviceRoot?.GetLeftPart(UriPartial.Authority),
            rememberLogin = settings.RememberLogin,
            autoOpen = settings.AutoOpenShortcut,
            tileName = settings.ShortcutTileName,
            tileId = settings.ShortcutTileId,
            waitMilliseconds = WaitMinutes * 60 * 1000
        });
        return reader.ReadToEnd().Replace("__CONFIG__", config, StringComparison.Ordinal);
    }
}
