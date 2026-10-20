using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace UGREENRemoteDrive.Native;

/// <summary>
/// Single-EXE launcher for pinned Tailscale with a private Windows adapter GUID. Native Windows SMB
/// still requires a kernel-network adapter; the signed Wintun DLL is extracted
/// on demand and the per-user daemon is started with a UAC prompt. No Windows
/// service, installer, scheduled task, Dokany or system Tailscale app is used.
/// </summary>
internal sealed class ManagedTailnet
{
    internal static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UGREEN Remote Drive Windows SMB");
    private static readonly string UserSid = WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("Windows-gebruikersidentiteit is niet beschikbaar.");
    internal static string PrivateSocket => @"\\.\pipe\tailscale-techbase-smb-" + UserSid;
    private static string AdapterName => "UGREENPortable" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(UserSid)))[..10];
    private static string StatePath => Path.Combine(DataRoot, "tailscaled.state");
    private static readonly object ExtractionLock = new();
    private static string? _components;

    // Only files embedded by the signed/verified build may be executed.
    internal static string Components
    {
        get
        {
            lock (ExtractionLock)
            {
                if (_components is not null) return _components;
                var binaries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in new[] { "tailscaled.exe", "tailscale.exe", "wintun.dll" })
                {
                    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                        "UGREEN.Portable." + name)
                        ?? throw new FileNotFoundException("De portable EXE bevat niet alle Tailscale-onderdelen.");
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    binaries.Add(name, buffer.ToArray());
                }

                var digest = Convert.ToHexString(SHA256.HashData(
                    binaries["tailscaled.exe"].Concat(binaries["tailscale.exe"])
                        .Concat(binaries["wintun.dll"]).ToArray()))[..24];
                var directory = Path.Combine(DataRoot, "Components", digest);
                Directory.CreateDirectory(directory);
                foreach (var (name, bytes) in binaries)
                {
                    var target = Path.Combine(directory, name);
                    var expected = SHA256.HashData(bytes);
                    if (!File.Exists(target))
                    {
                        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                                       FileShare.None, 65536, FileOptions.WriteThrough))
                                file.Write(bytes);
                            try { File.Move(temp, target); }
                            catch (IOException) when (File.Exists(target)) { }
                        }
                        finally { if (File.Exists(temp)) File.Delete(temp); }
                    }
                    using var existing = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), expected))
                        throw new IOException("Een uitgepakt Tailscale-onderdeel is gewijzigd: " + name +
                            ". Sluit de app en verwijder de Components-map om opnieuw uit te pakken.");
                    CryptographicOperations.ZeroMemory(bytes);
                }
                using (var notices = Assembly.GetExecutingAssembly().GetManifestResourceStream("UGREEN.Portable.NOTICES.txt"))
                {
                    if (notices is not null)
                    {
                        using var destination = File.Create(Path.Combine(directory, "THIRD-PARTY-NOTICES.txt"));
                        notices.CopyTo(destination);
                    }
                }
                _components = directory;
                return directory;
            }
        }
    }

    internal static int SelfTest()
    {
        try
        {
            var directory = Components;
            foreach (var name in new[] { "tailscaled.exe", "tailscale.exe", "wintun.dll" })
            {
                var path = Path.Combine(directory, name);
                using var file = File.OpenRead(path);
                if (file.Length < 64 || file.ReadByte() != 'M' || file.ReadByte() != 'Z')
                    return 2;
            }
            var cli = RunAsync(["version"], TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            return cli.ExitCode;
        }
        catch { return 1; }
    }

    public async Task EnsureStartedAsync()
    {
        if (await IsDaemonAvailableAsync()) return;
        Directory.CreateDirectory(DataRoot);
        var executable = Path.Combine(Components, "tailscaled.exe");
        var info = new ProcessStartInfo(executable)
        {
            Verb = "runas", // Elevate ONLY the private network process, never Explorer/SMB drive mapping.
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Components
        };
        info.ArgumentList.Add("--windows-mode=dev");
        info.ArgumentList.Add("--tun=" + AdapterName);
        info.ArgumentList.Add("--state=" + StatePath);
        info.ArgumentList.Add("--socket=" + PrivateSocket);
        info.ArgumentList.Add("--no-logs-no-support");
        Process started;
        try
        {
            started = Process.Start(info)
                ?? throw new IOException("Windows kon de ingebouwde Tailscale-adapter niet starten.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException(
                "De Windows-beheerdersvraag is geannuleerd. Native Windows SMB via Tailscale heeft Wintun-toegang nodig.", ex);
        }

        // WindowsModeDev uses a per-user named pipe. Elevation with a DIFFERENT
        // account changes the SID: refuse to use an unrelated machine identity.
        using (started)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (await IsDaemonAvailableAsync()) return;
                if (started.HasExited)
                    throw new IOException("De eigen Tailscale-adapter is gestopt (Windows-code " + started.ExitCode +
                        "). Controleer of je werkcomputer de Wintun-driver en dit programma toestaat.");
                await Task.Delay(500);
            }
        }
        throw new IOException("Het eigen Tailscale-besturingskanaal antwoordt niet. Dit kan door een adapterconflict of Windows-beleid komen; het bewijst geen aanmelding met een ander account.");
    }

    public async Task<string> StatusAsync()
    {
        var result = await RunAsync(["status", "--json"], TimeSpan.FromSeconds(15));
        if (result.ExitCode != 0)
            throw new IOException("De portable Tailscale-daemon is niet bereikbaar. " +
                "Klik opnieuw op 'Ingebouwde Tailscale + SMB'.");
        using var document = JsonDocument.Parse(result.Output);
        return document.RootElement.GetRawText();
    }

    public async Task<bool> IsOnlineAsync()
    {
        try
        {
            using var result = JsonDocument.Parse(await StatusAsync());
            return result.RootElement.TryGetProperty("BackendState", out var state)
                && state.GetString() == "Running"
                && result.RootElement.TryGetProperty("TUN", out var tun)
                && tun.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    private static async Task<bool> IsDaemonAvailableAsync()
    {
        try { return (await RunAsync(["status", "--json"], TimeSpan.FromSeconds(3))).ExitCode == 0; }
        catch { return false; }
    }

    public async Task RegisterAsync(SecureString key)
    {
        await EnsureStartedAsync();
        if (key.Length is < 10 or > 4096)
            throw new ArgumentException("Gebruik een geldige, eenmalige Tailscale-authkey.");

        Directory.CreateDirectory(DataRoot);
        var secretPath = Path.Combine(DataRoot, "auth-" + Guid.NewGuid().ToString("N") + ".key");
        IntPtr secretPointer = IntPtr.Zero;
        try
        {
            secretPointer = Marshal.SecureStringToGlobalAllocUnicode(key);
            var codeUnits = new short[key.Length];
            var chars = new char[key.Length];
            try
            {
                Marshal.Copy(secretPointer, codeUnits, 0, codeUnits.Length);
                for (var i = 0; i < chars.Length; i++) chars[i] = (char)codeUnits[i];
                var secretBytes = Encoding.UTF8.GetBytes(chars);
                try
                {
                    // The private user-profile directory inherits that user's ACL.
                    using var file = new FileStream(secretPath, FileMode.CreateNew, FileAccess.Write,
                        FileShare.Read, 4096, FileOptions.WriteThrough);
                    file.Write(secretBytes);
                    file.Flush(true);
                }
                finally { CryptographicOperations.ZeroMemory(secretBytes); }
            }
            finally
            {
                Array.Clear(chars);
                Array.Clear(codeUnits);
            }
            var result = await RunAsync(["up", "--timeout=30s", "--auth-key=file:" + secretPath,
                "--hostname=ugreen-remote-drive-portable", "--accept-dns=false",
                "--accept-routes=false", "--shields-up"], TimeSpan.FromSeconds(120));
            if (result.ExitCode != 0 && BackendState(await StatusAsync()) != "NeedsMachineAuth")
                throw new IOException("Tailscale-registratie is mislukt. Controleer de authkey, tailnet en " +
                    "eventuele apparaatgoedkeuring.");
        }
        finally
        {
            if (secretPointer != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(secretPointer);
            try { if (File.Exists(secretPath)) File.Delete(secretPath); } catch { }
        }
    }

    public async Task DisconnectAsync()
    {
        var result = await RunAsync(["down"], TimeSpan.FromSeconds(20));
        if (result.ExitCode != 0)
            throw new IOException("De portable Tailscale-verbinding kon niet worden gestopt.");
    }

    public async Task ResumeAsync()
    {
        var result = await RunAsync(["up", "--timeout=20s", "--hostname=ugreen-remote-drive-portable", "--accept-dns=false",
            "--accept-routes=false", "--shields-up"], TimeSpan.FromSeconds(30));
        if (result.ExitCode != 0 && BackendState(await StatusAsync()) is not ("NeedsMachineAuth" or "NeedsLogin"))
            throw new IOException("Tailscale kon niet hervatten. Controleer internet en de toestemming van dit apparaat.");
    }

    public async Task LogoutAsync()
    {
        var result = await RunAsync(["logout"], TimeSpan.FromSeconds(20));
        if (result.ExitCode != 0) throw new IOException("De eigen Tailscale-toestemming kon niet worden gewist.");
    }

    private static string? BackendState(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("BackendState").GetString();
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string[] args, TimeSpan timeout)
    {
        var cli = Path.Combine(Components, "tailscale.exe");
        var info = new ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Components
        };
        info.ArgumentList.Add("--socket=" + PrivateSocket);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["TS_NO_LOGS_NO_SUPPORT"] = "true";
        using var process = Process.Start(info) ??
            throw new IOException("De ingebouwde Tailscale-CLI kon niet starten.");
        using var stop = new CancellationTokenSource(timeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(stop.Token);
            var stderr = process.StandardError.ReadToEndAsync(stop.Token);
            await process.WaitForExitAsync(stop.Token);
            _ = await stderr; // Never leak CLI authentication URLs or other diagnostics.
            return (process.ExitCode, await stdout);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("De portable Tailscale-aanvraag duurde te lang.");
        }
    }
}
