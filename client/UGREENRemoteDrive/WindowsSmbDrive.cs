using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;

namespace UGREENRemoteDrive;

internal sealed record WindowsDriveSlot(bool Occupied, string? Root = null);
internal sealed class WindowsSmbMappingException(int error) : IOException(new SmbSignInResult(error).Message)
{
    public int Error { get; } = error;
}
internal interface IWindowsDriveApi
{
    WindowsDriveSlot Inspect(string letter);
    int Map(string letter, string root);
    int Unmap(string letter);
}

// Map directly through Windows; no Dokany callbacks, polling, or file caching.
internal sealed class WindowsSmbDrive(IWindowsDriveApi api)
{
    public string? Letter { get; private set; }
    public string? Root { get; private set; }
    public bool OwnsMapping { get; private set; }
    public bool IsMapped => Letter is not null;

    internal static string ValidateLetter(string letter) => Native.NativeSmb.ValidateDrive(letter);

    public void Connect(string letter, string root)
    {
        letter = ValidateLetter(letter);
        root = SmbSignInService.ValidateRoot(root);
        if (IsMapped) throw new InvalidOperationException("Ontkoppel eerst de huidige schijf.");
        var slot = api.Inspect(letter);
        var reuse = slot.Root?.Equals(root, StringComparison.OrdinalIgnoreCase) == true;
        if (slot.Occupied && !reuse)
            throw new IOException($"{letter} is al voor een andere schijf in gebruik. Kies een vrije schijfletter bij Instellingen.");
        if (!reuse)
        {
            var result = api.Map(letter, root);
            if (result == 85) throw new IOException($"{letter} is inmiddels in gebruik. Kies een andere schijfletter.");
            if (result != 0) throw new WindowsSmbMappingException(result);
        }
        Letter = letter; Root = root; OwnsMapping = !reuse;
    }

    // Never force-close open files, unmap by server name, or delete other mappings.
    // A mapping that predated this app is reused but remains Windows/user-owned.
    public bool Disconnect()
    {
        if (Letter is null) return false;
        var slot = api.Inspect(Letter);
        var removed = false;
        if (OwnsMapping && slot.Root?.Equals(Root, StringComparison.OrdinalIgnoreCase) == true)
        {
            var result = api.Unmap(Letter);
            if (result is not (0 or 2250))
                throw new IOException($"Windows kan de schijf nog niet ontkoppelen (code {result}). Sluit eerst alle geopende bestanden.");
            removed = true;
        }
        Letter = Root = null; OwnsMapping = false;
        return removed;
    }
}

internal sealed class WindowsDriveApi : IWindowsDriveApi
{
    public WindowsDriveSlot Inspect(string letter)
    {
        var buffer = new StringBuilder(1024);
        var length = buffer.Capacity;
        var result = WNetGetConnectionW(letter, buffer, ref length);
        if (result == 234 && length <= 32768)
        {
            buffer = new StringBuilder(length);
            result = WNetGetConnectionW(letter, buffer, ref length);
        }
        if (result is 0 or 1201) return new(true, buffer.Length > 0 ? buffer.ToString().TrimEnd('\\') : null);
        if (result != 2250) throw new IOException($"Windows kon de schijfletter niet controleren (code {result}).");
        var device = new StringBuilder(32768);
        if (QueryDosDeviceW(letter, device, device.Capacity) != 0) return new(true);
        var error = Marshal.GetLastWin32Error();
        if (error is not (2 or 3)) throw new IOException($"Windows kon de schijfletter niet controleren (code {error}).");
        return new(false);
    }

    public int Map(string letter, string root)
    {
        var resource = new NetworkResource { Type = 1, LocalName = letter, RemoteName = root };
        return WNetAddConnection2W(ref resource, IntPtr.Zero, null, 0); // Session mapping; no persistent profile changes.
    }
    public int Unmap(string letter) => WNetCancelConnection2W(letter, 0, false);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetworkResource
    {
        public uint Scope, Type, DisplayType, Usage;
        public string? LocalName, RemoteName, Comment, Provider;
    }
    [DllImport("mpr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WNetAddConnection2W(ref NetworkResource resource, IntPtr password, string? user, uint flags);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WNetGetConnectionW(string localName, StringBuilder remoteName, ref int length);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WNetCancelConnection2W(string name, uint flags, [MarshalAs(UnmanagedType.Bool)] bool force);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string device, StringBuilder target, int length);
}

// Keep credentials scoped to the configured hostname while Windows connects to
// the exact authenticated peer's IPv4. No public DNS resolution or credential copy.
internal sealed class PeerSmbConnector(string logicalRoot, string peerRoot) : ISmbConnector
{
    public int Connect(string root, string userName, SecureString password)
    {
        if (!SmbSignInService.ValidateRoot(root).Equals(logicalRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("De NAS-aanmelding hoort niet bij dit adres.");
        return new WindowsSmbConnector().Connect(peerRoot, userName, password);
    }
}

internal sealed class WindowsSmbTailnet(string host)
{
    private readonly Native.ManagedTailnet _tailnet = new();
    public string Host { get; } = EmbeddedTailscaleBackend.ValidateHost(host);
    public string ShareRoot => @"\\" + Host + @"\Techbase";
    public string? PeerRoot { get; private set; }
    public Task EnsureStartedAsync() => _tailnet.EnsureStartedAsync();
    public Task AuthorizeAsync(SecureString key) => _tailnet.RegisterAsync(key);
    public Task ResumeAsync() => _tailnet.ResumeAsync();
    public Task LogoutAsync() => _tailnet.LogoutAsync();

    public async Task<TailnetStatus> GetStatusAsync()
    {
        var json = await _tailnet.StatusAsync();
        using var document = JsonDocument.Parse(json);
        var state = document.RootElement.GetProperty("BackendState").GetString() ?? "NoState";
        PeerRoot = null;
        if (state == "Running")
        {
            // Includes TUN, exact name/IP, authenticated peer, online, IPv4 checks.
            PeerRoot = Native.NativeSmb.ResolveTailnetShare(Host, json);
        }
        return new(state, PeerRoot is not null, PeerRoot is not null, false, "unknown");
    }
}
