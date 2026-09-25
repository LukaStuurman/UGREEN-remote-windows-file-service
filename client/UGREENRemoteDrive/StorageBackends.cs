using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UGREENRemoteDrive;

internal sealed record FileEntry(string Name, bool IsDirectory, long Size, DateTimeOffset CreatedUtc, DateTimeOffset ModifiedUtc);
internal sealed record DiskSpace(long Available, long Total, long Free);

internal interface IFileBackend
{
    FileEntry Stat(string path);
    IReadOnlyList<FileEntry> List(string path);
    byte[] Read(string path, long offset, int length);
    int Write(string path, long offset, byte[] data);
    void CreateFile(string path);
    void CreateDirectory(string path);
    void Resize(string path, long length);
    void Rename(string source, string target, bool replace);
    void Delete(string path, bool directory);
    void SetModifiedTime(string path, DateTimeOffset modifiedUtc);
    DiskSpace GetSpace();
}

internal static class TechbaseSharePath
{
    private static readonly Regex UncRootPattern = new(
        @"^\\\\[^\\/]+\\Techbase\\?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string ValidateUncRoot(string path)
    {
        var candidate = path.Trim();
        if (!UncRootPattern.IsMatch(candidate))
            throw new ArgumentException("Gebruik uitsluitend de SMB-share-root \\\\NASNAAM\\Techbase; andere shares of submappen zijn niet toegestaan.");
        return candidate.TrimEnd('\\', '/');
    }
}

internal sealed class RemoteBackend(RemoteBridge bridge) : IFileBackend
{
    public bool IsAuthenticated => bridge.IsAuthenticated;

    public bool Authenticate() => bridge.TryAuthenticate();

    public FileEntry Stat(string path) => Deserialize<FileEntry>(Send("GET", "stat", Query(path)));

    public IReadOnlyList<FileEntry> List(string path) => Deserialize<List<FileEntry>>(Send("GET", "list", Query(path)));

    public byte[] Read(string path, long offset, int length)
    {
        var reply = bridge.Send("GET", bridge.BuildApiUrl("read", new Dictionary<string, string>
        {
            ["path"] = Normalize(path), ["offset"] = offset.ToString(), ["length"] = length.ToString()
        }), binaryResponse: true);
        EnsureSuccess(reply);
        return Convert.FromBase64String(reply.Body);
    }

    public int Write(string path, long offset, byte[] data)
    {
        var reply = bridge.Send("PUT", bridge.BuildApiUrl("write", new Dictionary<string, string>
        {
            ["path"] = Normalize(path), ["offset"] = offset.ToString()
        }), binaryBody: data);
        EnsureSuccess(reply);
        using var document = JsonDocument.Parse(reply.Body);
        return document.RootElement.GetProperty("written").GetInt32();
    }

    public void CreateFile(string path) => EnsureSuccess(bridge.Send("POST", bridge.BuildApiUrl("create", Query(path))));
    public void CreateDirectory(string path) => EnsureSuccess(bridge.Send("POST", bridge.BuildApiUrl("mkdir", Query(path))));
    public void Resize(string path, long length) => EnsureSuccess(bridge.Send("POST", bridge.BuildApiUrl("resize", new Dictionary<string, string>
    {
        ["path"] = Normalize(path), ["size"] = length.ToString()
    })));

    public void Rename(string source, string target, bool replace)
    {
        var body = JsonSerializer.Serialize(new { source = Normalize(source), target = Normalize(target), replace });
        EnsureSuccess(bridge.Send("POST", bridge.BuildApiUrl("rename"), jsonBody: body));
    }

    public void Delete(string path, bool directory) => EnsureSuccess(bridge.Send("DELETE", bridge.BuildApiUrl(directory ? "directory" : "file", Query(path))));

    public void SetModifiedTime(string path, DateTimeOffset modifiedUtc)
    {
        var body = JsonSerializer.Serialize(new { path = Normalize(path), modifiedUtcEpoch = modifiedUtc.ToUnixTimeMilliseconds() / 1000d });
        EnsureSuccess(bridge.Send("POST", bridge.BuildApiUrl("times"), jsonBody: body));
    }

    public DiskSpace GetSpace() => Deserialize<DiskSpace>(Send("GET", "space"));

    private string Send(string method, string route, IReadOnlyDictionary<string, string>? query = null)
    {
        var reply = bridge.Send(method, bridge.BuildApiUrl(route, query));
        EnsureSuccess(reply);
        return reply.Body;
    }

    private static T Deserialize<T>(string body) => JsonSerializer.Deserialize<T>(body, JsonDefaults.Options)
        ?? throw new DriveApiException(500, "De NAS-service gaf een leeg antwoord.");

    private static IReadOnlyDictionary<string, string> Query(string path) => new Dictionary<string, string> { ["path"] = Normalize(path) };

    private static string Normalize(string path) => path.TrimStart('\\', '/').Replace('\\', '/');

    private static void EnsureSuccess(BridgeReply reply)
    {
        if (reply.Ok) return;
        var message = reply.Error ?? "Aanvraag mislukt.";
        try
        {
            using var document = JsonDocument.Parse(reply.Body);
            if (document.RootElement.TryGetProperty("error", out var error)) message = error.GetString() ?? message;
        }
        catch { }
        throw new DriveApiException(reply.Status, message);
    }
}

internal sealed class SmbBackend(string shareRoot) : IFileBackend
{
    private readonly string _root = Path.GetFullPath(TechbaseSharePath.ValidateUncRoot(shareRoot));

    public bool CanReach() => Directory.Exists(_root);

    public FileEntry Stat(string path)
    {
        var resolved = Resolve(path);
        FileSystemInfo info = Directory.Exists(resolved) ? new DirectoryInfo(resolved) : new FileInfo(resolved);
        if (!info.Exists) throw new FileNotFoundException("Bestand niet gevonden.", resolved);
        return ToEntry(info);
    }

    public IReadOnlyList<FileEntry> List(string path)
    {
        var directory = new DirectoryInfo(Resolve(path));
        if (!directory.Exists) throw new DirectoryNotFoundException("Map niet gevonden.");
        return directory.EnumerateFileSystemInfos().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Select(ToEntry).ToArray();
    }

    public byte[] Read(string path, long offset, int length)
    {
        using var stream = new FileStream(Resolve(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset >= stream.Length) return [];
        stream.Position = offset;
        var buffer = new byte[Math.Min(length, checked((int)Math.Min(int.MaxValue, stream.Length - offset)))];
        var read = stream.Read(buffer, 0, buffer.Length);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    public int Write(string path, long offset, byte[] data)
    {
        using var stream = new FileStream(Resolve(path), FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.Position = offset < 0 ? stream.Length : offset;
        stream.Write(data);
        stream.Flush();
        return data.Length;
    }

    public void CreateFile(string path)
    {
        using var _ = new FileStream(Resolve(path), FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    public void CreateDirectory(string path)
    {
        var resolved = Resolve(path);
        var parent = Path.GetDirectoryName(resolved);
        if (parent is null || !Directory.Exists(parent)) throw new DirectoryNotFoundException("Bovenliggende map niet gevonden.");
        Directory.CreateDirectory(resolved);
    }

    public void Resize(string path, long length)
    {
        using var stream = new FileStream(Resolve(path), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.SetLength(length);
    }

    public void Rename(string source, string target, bool replace)
    {
        var from = Resolve(source);
        var to = Resolve(target);
        if (Directory.Exists(from))
        {
            if (Directory.Exists(to) || File.Exists(to)) throw new IOException("De doelnaam bestaat al.");
            Directory.Move(from, to);
        }
        else if (replace)
        {
            File.Move(from, to, true);
        }
        else
        {
            File.Move(from, to);
        }
    }

    public void Delete(string path, bool directory)
    {
        var resolved = Resolve(path);
        if (directory) Directory.Delete(resolved, false);
        else File.Delete(resolved);
    }

    public void SetModifiedTime(string path, DateTimeOffset modifiedUtc) => File.SetLastWriteTimeUtc(Resolve(path), modifiedUtc.UtcDateTime);

    public DiskSpace GetSpace()
    {
        var root = Path.GetPathRoot(_root) ?? _root;
        var drive = new DriveInfo(root);
        return new DiskSpace(drive.AvailableFreeSpace, drive.TotalSize, drive.AvailableFreeSpace);
    }

    private string Resolve(string relative)
    {
        var normalized = relative.Replace('/', '\\').TrimStart('\\');
        if (normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
            throw new UnauthorizedAccessException("Ongeldig pad.");
        var full = Path.GetFullPath(Path.Combine(_root, normalized));
        var rootPrefix = _root.TrimEnd('\\') + "\\";
        if (!full.Equals(_root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Pad valt buiten de SMB-share.");
        return full;
    }

    private static FileEntry ToEntry(FileSystemInfo info)
    {
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Reparse points are not followed.");
        var directory = (info.Attributes & FileAttributes.Directory) != 0;
        var size = directory ? 0 : ((FileInfo)info).Length;
        return new FileEntry(info.Name, directory, size, info.CreationTimeUtc, info.LastWriteTimeUtc);
    }
}

internal sealed class BackendSelector(RemoteBackend remote, SmbBackend? smb)
{
    private int _smbReachable;
    private int _smbProbeInProgress;
    public RemoteBackend Remote { get; } = remote;
    public SmbBackend? Smb { get; } = smb;
    public bool SmbReachable => Volatile.Read(ref _smbReachable) != 0;
    public string Mode => SmbReachable ? "LAN-SMB" : Remote.IsAuthenticated ? "UGREENlink" : "UGREENlink (aanmelding vereist)";
    public IFileBackend Current => SmbReachable && Smb is not null ? Smb : Remote;

    public async Task ProbeSmbAsync()
    {
        if (Smb is null)
        {
            Volatile.Write(ref _smbReachable, 0);
            return;
        }
        if (Interlocked.CompareExchange(ref _smbProbeInProgress, 1, 0) != 0) return;
        try
        {
            var reachable = await Task.Run(Smb.CanReach).WaitAsync(TimeSpan.FromSeconds(2));
            Volatile.Write(ref _smbReachable, reachable ? 1 : 0);
        }
        catch
        {
            Volatile.Write(ref _smbReachable, 0);
        }
        finally
        {
            Volatile.Write(ref _smbProbeInProgress, 0);
        }
    }
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
}
