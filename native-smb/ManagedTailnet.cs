using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace UGREENRemoteDrive.Native;

/// <summary>
/// A private, installer-managed Tailscale daemon backed by the Wintun adapter.
/// It does NOT reuse the embedded tsnet helper, the ordinary Tailscale service,
/// or the official client's identity / control pipe.
/// </summary>
internal sealed class ManagedTailnet
{
    internal const string PrivateSocket = @"\\.\pipe\ProtectedPrefix\Administrators\UGREENRemoteDriveNative\tailscaled";
    internal const string TaskName = "UGREENRemoteDriveNativeTailnet";
    private static string Executable => Path.Combine(AppContext.BaseDirectory, "tailnet", "tailscale.exe");

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public async Task<string> StatusAsync()
    {
        var result = await RunAsync(["status", "--json"], TimeSpan.FromSeconds(15));
        if (result.ExitCode != 0)
            throw new InvalidOperationException("De privé-Tailscale-service reageert niet. Controleer de Windows-taak '" +
                TaskName + "' (en of de Wintun-adapter gestart kan worden).");
        using var doc = JsonDocument.Parse(result.Output);
        return doc.RootElement.GetRawText();
    }

    public async Task<bool> IsOnlineAsync()
    {
        try
        {
            using var status = JsonDocument.Parse(await StatusAsync());
            return status.RootElement.TryGetProperty("BackendState", out var state) && state.GetString() == "Running"
                && status.RootElement.TryGetProperty("TUN", out var tun) && tun.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    public async Task RegisterAsync(SecureString key)
    {
        if (!IsAdministrator())
            throw new InvalidOperationException("Start de Native SMB-app eenmalig als administrator om Tailscale te registreren. Daarna is dat niet meer nodig.");
        if (key.Length is < 10 or > 4096) throw new ArgumentException("Vul een geldige Tailscale-authkey in.");
        // Never put the secret in argv, log output, registry or persistent settings.
        // The CLI's --auth-key=file: form reads this private transient file.
        var path = Path.Combine(Path.GetTempPath(), "ugreen-tailnet-" + Guid.NewGuid().ToString("N") + ".key");
        IntPtr bytes = IntPtr.Zero;
        try
        {
            bytes = System.Runtime.InteropServices.Marshal.SecureStringToGlobalAllocUnicode(key);
            var chars = new char[key.Length];
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(bytes, chars, 0, chars.Length);
                var secret = Encoding.UTF8.GetBytes(chars);
                try
                {
                    using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        file.Write(secret);
                }
                finally { Array.Clear(secret); }
            }
            finally { Array.Clear(chars); }
            var result = await RunAsync(["up", "--auth-key=file:" + path,
                "--hostname=ugreen-remote-drive-native", "--accept-dns=false",
                "--accept-routes=false", "--shields-up"], TimeSpan.FromSeconds(120));
            if (result.ExitCode != 0)
                throw new InvalidOperationException("De Tailscale-registratie is niet gelukt. Controleer de authkey en eventuele apparaatgoedkeuring. " +
                    "Status: " + result.ExitCode);
        }
        finally
        {
            if (bytes != IntPtr.Zero) System.Runtime.InteropServices.Marshal.ZeroFreeGlobalAllocUnicode(bytes);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    public async Task DisconnectAsync()
    {
        var result = await RunAsync(["down"], TimeSpan.FromSeconds(20));
        if (result.ExitCode != 0)
            throw new IOException("De privé-Tailscale-verbinding kon niet worden gestopt.");
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string[] args, TimeSpan timeout)
    {
        if (!File.Exists(Executable))
            throw new FileNotFoundException("De meegeleverde Tailscale-client ontbreekt. Installeer de volledige Native SMB-editie.");
        var info = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("--socket=" + PrivateSocket);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["TS_NO_LOGS_NO_SUPPORT"] = "true";
        using var process = Process.Start(info) ?? throw new IOException("De privé-Tailscale-client kon niet starten.");
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cancel.Token);
            var stderr = process.StandardError.ReadToEndAsync(cancel.Token);
            await process.WaitForExitAsync(cancel.Token);
            var output = await stdout;
            _ = await stderr; // Do not expose raw CLI diagnostics: they can contain auth URLs.
            return (process.ExitCode, output);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("De privé-Tailscale-bewerking duurde te lang.");
        }
    }
}
