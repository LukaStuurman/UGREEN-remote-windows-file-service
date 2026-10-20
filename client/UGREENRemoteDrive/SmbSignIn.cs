using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;

namespace UGREENRemoteDrive;

internal sealed class SmbCredential(string userName, SecureString password) : IDisposable
{
    public string UserName { get; } = userName;
    public SecureString Password { get; } = password;
    public void Dispose() => Password.Dispose();
}

internal interface ISmbCredentialStore
{
    SmbCredential? Load(string root);
    void Save(string root, string userName, SecureString password);
    void Delete(string root);
}

internal interface ISmbConnector
{
    int Connect(string root, string userName, SecureString password);
}

internal sealed record SmbSignInResult(int Error, string? Warning = null)
{
    public bool Success => Error == 0;
    public string Message => Error switch
    {
        0 => "Aangemeld bij de NAS.",
        5 => "Deze NAS-gebruiker heeft geen toegang tot de Techbase-share. Controleer de SMB-rechten in UGOS.",
        86 or 1326 or 2202 => "De NAS-gebruikersnaam of het wachtwoord klopt niet. Gebruik je lokale UGOS-account.",
        1219 => "Windows is al met een ander account verbonden met deze NAS. Sluit die verbinding eerst zelf in Windows; de app verbreekt geen andere verbindingen.",
        1909 => "Het NAS-account is geblokkeerd. Controleer dit in UGOS voordat je opnieuw probeert.",
        _ => $"Aanmelden is mislukt (Windows-code {Error}). Controleer het netwerk, de VPN en de Techbase-share."
    };
}

internal sealed class SmbSignInService(ISmbCredentialStore store, ISmbConnector connector)
{
    public static SmbSignInService Create() => new(new WindowsSmbCredentialStore(), new WindowsSmbConnector());

    public static string ValidateRoot(string root)
    {
        var validated = TechbaseSharePath.ValidateUncRoot(root);
        var host = validated[2..validated.LastIndexOf('\\')];
        if (validated.Length > 260 || validated.Any(char.IsControl) ||
            Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
            throw new ArgumentException("Gebruik een geldig NAS-adres met uitsluitend de Techbase-share.");
        return validated;
    }

    public SmbSignInResult SignIn(string root, string userName, SecureString password, bool remember)
    {
        root = ValidateRoot(root);
        userName = userName.Trim();
        if (userName.Length is 0 or > 513 || userName.Any(char.IsControl))
            throw new ArgumentException("Vul je NAS-gebruikersnaam in.");
        if (password.Length is 0 or > 1280)
            throw new ArgumentException("Vul een wachtwoord in van maximaal 1280 tekens.");
        var result = new SmbSignInResult(connector.Connect(root, userName, password));
        if (!result.Success) return result; // Never remember an unsuccessful sign-in.
        try
        {
            if (remember) store.Save(root, userName, password);
            else store.Delete(root);
            return result;
        }
        catch
        {
            return result with { Warning = remember
                ? "Verbonden, maar Windows kon deze aanmelding niet onthouden."
                : "Verbonden, maar de eerder onthouden aanmelding kon niet worden gewist." };
        }
    }

    public SmbSignInResult? Restore(string root)
    {
        root = ValidateRoot(root);
        using var credential = store.Load(root);
        return credential is null ? null : new(connector.Connect(root, credential.UserName, credential.Password));
    }

    public void Forget(string root) => store.Delete(ValidateRoot(root));

    public static async Task<bool> CanContactServerAsync(string root, int timeoutMilliseconds = 1000)
    {
        root = ValidateRoot(root);
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMilliseconds));
        try
        {
            await client.ConnectAsync(root[2..root.LastIndexOf('\\')], 445, timeout.Token);
            return true;
        }
        catch { return false; }
    }
}

internal sealed class WindowsSmbConnector : ISmbConnector
{
    public int Connect(string root, string userName, SecureString password)
    {
        var resource = new NetworkResource { Type = 1, RemoteName = SmbSignInService.ValidateRoot(root) };
        var secret = Marshal.SecureStringToGlobalAllocUnicode(password);
        try { return WNetAddConnection2W(ref resource, secret, userName, 0); }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(secret); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetworkResource
    {
        public uint Scope, Type, DisplayType, Usage;
        public string? LocalName, RemoteName, Comment, Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WNetAddConnection2W(ref NetworkResource resource, IntPtr password, string userName, uint flags);
}

// Only the app's own Generic credentials are read/changed. Existing Windows SMB,
// Edge and WebView2 credentials are never imported or overwritten.
internal sealed class WindowsSmbCredentialStore : ISmbCredentialStore
{
    internal static string Target(string root) => "UGREENRemoteDrive:SMB:" + SmbSignInService.ValidateRoot(root).ToLowerInvariant();

    public SmbCredential? Load(string root)
    {
        if (!CredReadW(Target(root), 1, 0, out var buffer))
        {
            if (Marshal.GetLastWin32Error() == 1168) return null;
            throw new InvalidOperationException("Windows kon de onthouden NAS-aanmelding niet openen.");
        }
        var credential = Marshal.PtrToStructure<Credential>(buffer);
        var password = new SecureString();
        try
        {
            if (credential.BlobSize is 0 or > 2560 || credential.BlobSize % 2 != 0 ||
                credential.Blob == IntPtr.Zero || string.IsNullOrWhiteSpace(credential.UserName))
                throw new InvalidOperationException("De onthouden NAS-aanmelding is ongeldig.");
            for (var offset = 0; offset < credential.BlobSize; offset += 2)
                password.AppendChar((char)Marshal.ReadInt16(credential.Blob, offset));
            password.MakeReadOnly();
            return new SmbCredential(credential.UserName, password);
        }
        catch { password.Dispose(); throw; }
        finally
        {
            if (credential.Blob != IntPtr.Zero && credential.BlobSize <= 2560)
                for (var offset = 0; offset < credential.BlobSize; offset++) Marshal.WriteByte(credential.Blob, offset, 0);
            CredFree(buffer);
        }
    }

    public void Save(string root, string userName, SecureString password)
    {
        var target = Target(root);
        var secret = Marshal.SecureStringToGlobalAllocUnicode(password);
        try
        {
            var credential = new Credential
            {
                Type = 1, TargetName = target, UserName = userName,
                BlobSize = checked((uint)password.Length * 2), Blob = secret, Persist = 2
            };
            if (!CredWriteW(ref credential, 0))
                throw new InvalidOperationException("Windows kon de NAS-aanmelding niet onthouden.");
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(secret); }
    }

    public void Delete(string root)
    {
        if (!CredDeleteW(Target(root), 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new InvalidOperationException("Windows kon de onthouden NAS-aanmelding niet wissen.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern void CredFree(IntPtr credential);
}
