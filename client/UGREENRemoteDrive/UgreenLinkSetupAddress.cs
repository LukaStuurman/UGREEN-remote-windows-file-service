using System.Text.RegularExpressions;

namespace UGREENRemoteDrive;

internal enum UgreenLinkSetupKind
{
    Shortcut,
    NasPortal
}

internal sealed record UgreenLinkSetupTarget(UgreenLinkSetupKind Kind, Uri Address);

internal static class UgreenLinkSetupAddress
{
    private static readonly Regex NasIdPath = new("^/[A-Za-z0-9._-]{1,96}/?\\z", RegexOptions.CultureInvariant);

    public static UgreenLinkSetupTarget Parse(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (UgreenLinkAddress.IsAllowedOrigin(uri))
            {
                var shortcut = UgreenLinkAddress.Validate(value);
                var stableOrigin = new Uri(shortcut.GetLeftPart(UriPartial.Authority) + "/");
                return new UgreenLinkSetupTarget(UgreenLinkSetupKind.Shortcut, stableOrigin);
            }

            if (IsNasPortalAddress(uri))
                return new UgreenLinkSetupTarget(UgreenLinkSetupKind.NasPortal, uri);
        }

        throw new InvalidOperationException(
            "Vul het HTTPS-adres van de NAS in (https://ug.link/...) of het app-adres (ugapp.link/ugdocker.link). " +
            "Bij een NAS-adres kun je na aanmelden de Remote Drive-tegel openen; de app onthoudt daarna het app-adres zelf.");
    }

    public static bool IsNasPortalAddress(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.Host.Equals("ug.link", StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) && NasIdPath.IsMatch(uri.AbsolutePath);
}
