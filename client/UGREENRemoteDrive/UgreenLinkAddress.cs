namespace UGREENRemoteDrive;

internal static class UgreenLinkAddress
{
    public static Uri Validate(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsAllowedOrigin(uri))
            throw new InvalidOperationException(
                "Plak het HTTPS-adres van de UGREENlink-snelkoppeling (ugapp.link of ugdocker.link).");
        return uri;
    }

    public static bool IsAllowedOrigin(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.EndsWith(".ugapp.link", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".ugdocker.link", StringComparison.OrdinalIgnoreCase));
}
