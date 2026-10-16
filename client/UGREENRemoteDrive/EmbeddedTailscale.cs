using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UGREENRemoteDrive;

internal sealed record TailnetStatus(string State, bool Ready, bool NasFound, bool Authenticated, string ConnectionType);
internal sealed record TailnetReply(long Id, int Status, string? Error, JsonElement Value, int DataLength)
{
    public byte[] Data { get; set; } = [];
}

internal sealed class TailscaleBroker : IDisposable
{
    private readonly Process _process;
    private readonly Stream _input;
    private readonly SemaphoreSlim _writer = new(1, 1), _slots = new(8, 8);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<TailnetReply>> _pending = new();
    private long _nextId;
    private int _disposed;
    public bool IsAlive => Volatile.Read(ref _disposed) == 0 && !_process.HasExited;

    internal TailscaleBroker()
    {
        var executable = ExtractComponent();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--state-dir");
        start.ArgumentList.Add(Path.Combine(SettingsStore.AppDirectory, "Tailscale"));
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("TS_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("TSNET_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["TS_NO_LOGS_NO_SUPPORT"] = "true";
        _process = Process.Start(start) ?? throw new IOException("De ingebouwde verbinding kon niet starten.");
        _input = _process.StandardInput.BaseStream;
        // Never persist helper diagnostics: dependencies can include auth URLs.
        _ = Task.Run(async () => { try { await _process.StandardError.BaseStream.CopyToAsync(Stream.Null); } catch { } });
        _ = Task.Run(ReadReplies);
    }

    internal static string ExtractComponent()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("UGREENRemoteDrive.Embedded.UGREENTailscale.exe")
            ?? throw new IOException("De ingebouwde Tailscale-component ontbreekt. Installeer de nieuwste volledige app.");
        var hash = Convert.ToHexString(SHA256.HashData(resource)).ToLowerInvariant();
        resource.Position = 0;
        var folder = Path.Combine(SettingsStore.AppDirectory, "Components", hash);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "UGREENTailscale.exe");
        if (!File.Exists(target))
        {
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, System.IO.FileAccess.Write, FileShare.None)) resource.CopyTo(file);
                try { File.Move(temporary, target); } catch (IOException) when (File.Exists(target)) { }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        using (var file = File.OpenRead(target))
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(file), Convert.FromHexString(hash)))
                throw new IOException("De ingebouwde verbindingscomponent is gewijzigd. Installeer de app opnieuw.");
        using var notices = Assembly.GetExecutingAssembly().GetManifestResourceStream("UGREENRemoteDrive.Embedded.NOTICES.txt");
        if (notices is not null)
        {
            using var file = File.Create(Path.Combine(folder, "THIRD-PARTY-NOTICES.txt"));
            notices.CopyTo(file);
        }
        return target;
    }

    public TailnetReply Call(string operation, object? arguments = null, byte[]? data = null)
    {
        _slots.Wait();
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<TailnetReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (!IsAlive) throw new DriveDisconnectedException("De ingebouwde Tailscale-verbinding is gestopt. Verbind opnieuw.");
            if (data?.Length > 4 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(data));
            var fields = new Dictionary<string, object?> { ["id"] = id, ["op"] = operation, ["dataLength"] = data?.Length ?? 0 };
            if (arguments is not null)
                foreach (var property in JsonSerializer.SerializeToElement(arguments).EnumerateObject()) fields[property.Name] = property.Value;
            var header = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields) + "\n");
            if (header.Length > 65536) throw new ArgumentException("De aanvraag is te groot.");
            _pending[id] = completion;
            _writer.Wait();
            try { _input.Write(header); if (data is not null) _input.Write(data); _input.Flush(); }
            finally { _writer.Release(); }
            return completion.Task.WaitAsync(TimeSpan.FromSeconds(35)).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            Dispose(); // No automatic replay of an uncertain write.
            throw new DriveDisconnectedException("De Tailscale-aanvraag duurde te lang. Controleer de verbinding en verbind opnieuw.");
        }
        finally { _pending.TryRemove(id, out _); _slots.Release(); }
    }

    private void ReadReplies()
    {
        try
        {
            using var stream = new BufferedStream(_process.StandardOutput.BaseStream);
            while (Volatile.Read(ref _disposed) == 0)
            {
                using var header = new MemoryStream();
                for (var count = 0; ; count++)
                {
                    var value = stream.ReadByte();
                    if (value < 0) throw new EndOfStreamException();
                    if (count >= 16 * 1024 * 1024) throw new InvalidDataException();
                    if (value == '\n') break;
                    header.WriteByte((byte)value);
                }
                var reply = JsonSerializer.Deserialize<TailnetReply>(header.ToArray(), JsonDefaults.Options) ?? throw new InvalidDataException();
                if (reply.DataLength is < 0 or > (4 * 1024 * 1024)) throw new InvalidDataException();
                reply.Data = new byte[reply.DataLength];
                stream.ReadExactly(reply.Data);
                if (!_pending.TryGetValue(reply.Id, out var completion)) throw new InvalidDataException();
                completion.TrySetResult(reply);
            }
        }
        catch { Dispose(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var pending in _pending.Values)
            pending.TrySetException(new DriveDisconnectedException("De ingebouwde Tailscale-verbinding is gestopt."));
        try { _input.Close(); } catch { }
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
    }

    internal static byte[] SecretBytes(SecureString secret)
    {
        var pointer = Marshal.SecureStringToGlobalAllocUnicode(secret);
        try { var bytes = new byte[secret.Length * 2]; Marshal.Copy(pointer, bytes, 0, bytes.Length); return bytes; }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(pointer); }
    }
}

internal sealed class EmbeddedTailscaleBackend : IFileBackend, IHandleFileBackend, ISmbConnector, IDisposable
{
    private readonly TailscaleBroker _broker;
    private readonly RemoteReadAheadCache _reads = new();
    private int _authenticated, _generation;
    public string Host { get; }
    public string ShareRoot => @"\\" + Host + @"\Techbase";
    public bool IsAuthenticated => Volatile.Read(ref _authenticated) != 0 && _broker.IsAlive;
    public bool IsRunning => _broker.IsAlive;

    public EmbeddedTailscaleBackend(string host)
    {
        Host = ValidateHost(host);
        _broker = new TailscaleBroker();
        try { Ensure(_broker.Call("configure", new { host = Host })); }
        catch { _broker.Dispose(); throw; }
    }
    internal static string ValidateHost(string value)
    {
        var host = value.Trim().TrimEnd('.').ToLowerInvariant();
        if (System.Net.IPAddress.TryParse(host, out var address))
        {
            var bytes = address.GetAddressBytes();
            if (bytes.Length == 4 && bytes[0] == 100 && bytes[1] is >= 64 and <= 127) return host;
        }
        else if (host.EndsWith(".ts.net", StringComparison.Ordinal) && host.Length <= 253 &&
                 host.Split('.').All(label => label.Length is > 0 and <= 63 && label[0] != '-' && label[^1] != '-' &&
                     label.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))) return host;
        throw new ArgumentException("Vul de volledige Tailscale NAS-naam (.ts.net) of het Tailscale IPv4-adres in, zonder https of sharepad.");
    }
    public TailnetStatus GetStatus()
    {
        var status = Deserialize<TailnetStatus>(Send("status"));
        Volatile.Write(ref _authenticated, status.Authenticated ? 1 : 0);
        return status;
    }
    public void Authorize(SecureString key)
    {
        var data = TailscaleBroker.SecretBytes(key);
        try { Ensure(_broker.Call("authorize", data: data)); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }
    public IBackendFileHandle OpenFile(string path, DokanNet.FileAccess access, FileShare sharing, FileMode mode,
        FileOptions options, FileAttributes attributes, bool directory)
    {
        var disposition = mode switch { FileMode.Open => 1, FileMode.CreateNew => 2, FileMode.OpenOrCreate or FileMode.Append => 3, FileMode.Truncate => 4, FileMode.Create => 5, _ => throw new ArgumentException("Ongeldige openmodus.") };
        uint createOptions = directory ? 1u : 0x40u;
        if ((options & FileOptions.DeleteOnClose) != 0) createOptions |= 0x1000;
        if ((options & FileOptions.WriteThrough) != 0) createOptions |= 2;
        var reply = Send("open-handle", new { path = Normalize(path), access = unchecked((uint)access), sharing = (uint)sharing,
            disposition, options = createOptions, attributes = (uint)(attributes & ~FileAttributes.Directory) });
        return new EmbeddedFileHandle(this, reply.Value.GetProperty("handle").GetUInt64(), reply.Value.GetProperty("existed").GetBoolean());
    }
    internal TailnetReply HandleCall(string operation, ulong handle, object? arguments, byte[]? data)
    {
        var fields = new Dictionary<string, object?> { ["handle"] = handle };
        if (arguments is not null)
            foreach (var property in JsonSerializer.SerializeToElement(arguments).EnumerateObject()) fields[property.Name] = property.Value;
        return operation is "handle-read" or "handle-stat" ? Send(operation, fields, data) : Mutate(() => Send(operation, fields, data));
    }
    public void Logout() { Ensure(_broker.Call("logout")); Volatile.Write(ref _authenticated, 0); Invalidate(); }
    public int Connect(string root, string userName, SecureString password)
    {
        if (!SmbSignInService.ValidateRoot(root).Equals(ShareRoot, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Het NAS-adres is gewijzigd.");
        var data = TailscaleBroker.SecretBytes(password);
        try
        {
            var reply = _broker.Call("connect", new { user = userName }, data);
            Volatile.Write(ref _authenticated, reply.Status == 200 ? 1 : 0); Invalidate();
            return reply.Status switch { 200 => 0, 401 => 1326, 403 => 5, _ => 53 };
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }
    public FileEntry Stat(string path) => Deserialize<FileEntry>(Send("stat", new { path = Normalize(path) }));
    public IReadOnlyList<FileEntry> List(string path) => Deserialize<List<FileEntry>>(Send("list", new { path = Normalize(path) }));
    public DiskSpace GetSpace() => Deserialize<DiskSpace>(Send("space"));
    public byte[] Read(string path, long offset, int length)
    {
        var session = Volatile.Read(ref _generation);
        return _reads.Read(Normalize(path), offset, length, session,
            (name, position, count) => Send("read", new { path = name, offset = position, length = count }).Data,
            () => IsAuthenticated && session == Volatile.Read(ref _generation));
    }
    public int Write(string path, long offset, byte[] data) => Mutate(() => Send("write", new { path = Normalize(path), offset }, data).Value.GetProperty("written").GetInt32());
    public void CreateFile(string path) => Mutate(() => Send("create", new { path = Normalize(path) }));
    public void CreateDirectory(string path) => Mutate(() => Send("mkdir", new { path = Normalize(path) }));
    public void Resize(string path, long length) => Mutate(() => Send("resize", new { path = Normalize(path), size = length }));
    public void Rename(string source, string target, bool replace) => Mutate(() => Send("rename", new { path = Normalize(source), target = Normalize(target), replace }));
    public void Delete(string path, bool directory) => Mutate(() => Send("delete", new { path = Normalize(path), directory }));
    public void SetModifiedTime(string path, DateTimeOffset modifiedUtc) => Mutate(() => Send("times", new { path = Normalize(path), modified = modifiedUtc }));
    private static string Normalize(string path) => path.TrimStart('\\', '/').Replace('\\', '/');
    private void Invalidate() { Interlocked.Increment(ref _generation); _reads.Invalidate(); }
    private T Mutate<T>(Func<T> action) { Invalidate(); try { return action(); } finally { Invalidate(); } }
    private TailnetReply Send(string operation, object? arguments = null, byte[]? data = null)
    {
        var reply = _broker.Call(operation, arguments, data);
        if (reply.Status is 401 or 503) { Volatile.Write(ref _authenticated, 0); Invalidate(); }
        Ensure(reply); return reply;
    }
    private static void Ensure(TailnetReply reply) { if (reply.Status != 200) throw new DriveApiException(reply.Status, reply.Error ?? "De Tailscale/NAS-aanvraag is mislukt."); }
    private static T Deserialize<T>(TailnetReply reply) => reply.Value.Deserialize<T>(JsonDefaults.Options) ?? throw new InvalidDataException("Het NAS-antwoord ontbreekt.");
    public void Dispose() { Volatile.Write(ref _authenticated, 0); Invalidate(); _broker.Dispose(); }
}
