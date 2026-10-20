[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Native.Tests")]

using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;

namespace UGREENRemoteDrive.Native;

/// <summary>Only ever expose the existing Techbase share through the Windows SMB redirector.</summary>
internal static class NativeSmb
{
    public static string ValidateDrive(string text)
    {
        var letter = text.Trim().TrimEnd(':').ToUpperInvariant();
        if (letter.Length != 1 || letter[0] < 'D' || letter[0] > 'Z')
            throw new ArgumentException("Kies een schijfletter van D: tot Z:.");
        return letter + ":";
    }

    public static string ValidateLanShare(string text)
    {
        var root = text.Trim().TrimEnd('\\');
        if (!root.StartsWith(@"\\", StringComparison.Ordinal) || root.Length > 260 ||
            root.Contains('/') || root.Any(char.IsControl))
            throw new ArgumentException(@"Vul uitsluitend \\NASNAAM\Techbase in.");
        var parts = root[2..].Split('\\');
        if (parts.Length != 2 || !parts[1].Equals("Techbase", StringComparison.OrdinalIgnoreCase) ||
            !ValidHost(parts[0]))
            throw new ArgumentException(@"Alleen de bestaande SMB-share \\NASNAAM\Techbase is toegestaan.");
        return root;
    }

    public static string ResolveTailnetShare(string host, string statusJson)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        string? ip = null;
        if (IPAddress.TryParse(host, out var address))
        {
            if (!IsTailnetIpv4(address))
                throw new ArgumentException("Gebruik een Tailscale IPv4-adres binnen 100.64.0.0/10.");
            ip = address.ToString();
        }
        else
        {
            if (host.Length > 253 || !host.EndsWith(".ts.net", StringComparison.Ordinal) ||
                !ValidHost(host))
                throw new ArgumentException("Gebruik de volledige .ts.net-NAS-naam of een Tailscale IPv4-adres.");
        }

        using var doc = JsonDocument.Parse(statusJson);
        var json = doc.RootElement;
        if (!json.TryGetProperty("TUN", out var tun) || tun.ValueKind != JsonValueKind.True ||
            !json.TryGetProperty("BackendState", out var state) || state.GetString() != "Running")
            throw new InvalidOperationException("De ingebouwde Tailscale-netwerkadapter is nog niet online.");

        // Never use DNS or the Internet for an unverified NAS peer.
        if (!json.TryGetProperty("Peer", out var peers) || peers.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("De ingestelde NAS staat niet in de aangemelde Tailscale-peerlijst.");

        foreach (var peer in peers.EnumerateObject())
        {
            var value = peer.Value;
            if (!value.TryGetProperty("TailscaleIPs", out var addresses) ||
                addresses.ValueKind != JsonValueKind.Array) continue;
            var peerIps = addresses.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? "").Where(x => IPAddress.TryParse(x, out var a) && IsTailnetIpv4(a)).ToArray();
            var peerName = value.TryGetProperty("DNSName", out var dns) && dns.ValueKind == JsonValueKind.String
                ? (dns.GetString() ?? "").TrimEnd('.').ToLowerInvariant() : "";
            if ((ip is null && peerName != host) || (ip is not null && !peerIps.Contains(ip, StringComparer.Ordinal)))
                continue;
            if (value.TryGetProperty("Online", out var online) && online.ValueKind == JsonValueKind.False)
                throw new InvalidOperationException("De NAS staat als offline in Tailscale.");
            var destination = ip ?? peerIps.FirstOrDefault();
            if (destination is null)
                throw new InvalidOperationException("De aangemelde NAS heeft geen geldig Tailscale IPv4-adres.");
            return @"\\" + destination + @"\Techbase";
        }
        throw new InvalidOperationException("De opgegeven NAS is niet gevonden in de eigen Tailscale-peerlijst.");
    }

    private static bool ValidHost(string host) =>
        host.Length is > 0 and <= 253 && host.Split('.').All(label =>
            label.Length is > 0 and <= 63 && label[0] != '-' && label[^1] != '-' &&
            label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));

    private static bool IsTailnetIpv4(IPAddress a)
    {
        if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var bytes = a.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
    }

    public static void Connect(string drive, string root, string user, SecureString password)
    {
        drive = ValidateDrive(drive);
        root = ValidateLanShare(root);
        if (DriveInfo.GetDrives().Any(d => d.Name.Equals(drive + "\\", StringComparison.OrdinalIgnoreCase)))
            throw new IOException($"Schijfletter {drive} is al bezet. Koppel eerst de bestaande schijf los.");
        var resource = new NetworkResource { Type = 1, LocalName = drive, RemoteName = root };
        IntPtr pointer = IntPtr.Zero;
        try
        {
            if (password.Length > 0) pointer = Marshal.SecureStringToGlobalAllocUnicode(password);
            var result = WNetAddConnection2W(ref resource, pointer, string.IsNullOrWhiteSpace(user) ? null : user.Trim(), 1);
            if (result != 0)
                throw new IOException(result switch
                {
                    5 => "Geen toegang tot Techbase. Controleer de UGOS SMB-machtigingen. (5)",
                    53 or 67 => "De NAS-share is niet bereikbaar. Controleer Tailscale, SMB-poort 445 en de NAS. (" + result + ")",
                    86 or 1326 => "Ongeldige NAS-gebruikersnaam of wachtwoord. (" + result + ")",
                    1219 => "Windows heeft al een SMB-sessie met een andere NAS-gebruiker. (1219)",
                    85 => "Deze schijfletter is al in gebruik. (85)",
                    _ => $"Windows SMB kon de schijf niet koppelen (code {result})."
                });
        }
        finally { if (pointer != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(pointer); }
    }

    public static void Disconnect(string drive)
    {
        var result = WNetCancelConnection2W(ValidateDrive(drive), 1, false);
        if (result != 0 && result != 2250)
            throw new IOException($"Windows kon de schijf niet loskoppelen (code {result}). Sluit geopende bestanden eerst.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetworkResource
    {
        public uint Scope, Type, DisplayType, Usage;
        public string? LocalName, RemoteName, Comment, Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WNetAddConnection2W(ref NetworkResource resource, IntPtr password, string? user, uint flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WNetCancelConnection2W(string name, uint flags, [MarshalAs(UnmanagedType.Bool)] bool force);
}
