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

    public bool CanReach()
    {
        try
        {
            SmbPathGuard.EnsureNoReparsePoints(_root, _root);
            return Directory.Exists(_root);
        }
        catch
        {
            return false;
        }
    }

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
        SmbPathGuard.EnsureNoReparsePoints(_root, _root);
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
        SmbPathGuard.EnsureNoReparsePoints(_root, full);
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

internal static class SmbPathGuard
{
    public static void EnsureNoReparsePoints(string root, string fullPath)
    {
        root = Path.GetFullPath(root);
        fullPath = Path.GetFullPath(fullPath);

        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Pad valt buiten de SMB-share.");

        CheckComponent(root, isFinalComponent: false, allowMissing: false);
        if (relative == ".") return;

        var components = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < components.Length; index++)
        {
            current = Path.Combine(current, components[index]);
            if (!CheckComponent(current, isFinalComponent: index == components.Length - 1, allowMissing: true)) return;
        }
    }

    private static bool CheckComponent(string path, bool isFinalComponent, bool allowMissing)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            if (allowMissing) return false;
            throw;
        }
        catch (DirectoryNotFoundException)
        {
            if (allowMissing) return false;
            throw;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Reparse points are not followed.");
        if (!isFinalComponent && (attributes & FileAttributes.Directory) == 0)
            throw new UnauthorizedAccessException("Een bestand kan niet als bovenliggende map worden gebruikt.");
        return true;
    }
}

internal sealed class BackendSelector
{
    private readonly IFileBackend _remote;
    private readonly IFileBackend? _smb;
    private readonly Func<bool> _remoteIsAuthenticated;
    private readonly Func<bool> _smbAvailabilityProbe;
    private IFileBackend? _mountedBackend;
    private int _smbReachable;
    private int _smbProbeInProgress;

    public BackendSelector(RemoteBackend remote, SmbBackend? smb)
        : this(remote, smb, () => remote.IsAuthenticated, () => smb?.CanReach() ?? false) { }

    internal BackendSelector(IFileBackend remote, IFileBackend? smb,
        Func<bool> remoteIsAuthenticated, Func<bool> smbAvailabilityProbe)
    {
        _remote = remote;
        _smb = smb;
        _remoteIsAuthenticated = remoteIsAuthenticated;
        _smbAvailabilityProbe = smbAvailabilityProbe;
    }

    public IFileBackend Remote => _remote;
    public IFileBackend? Smb => _smb;
    public bool SmbReachable => Volatile.Read(ref _smbReachable) != 0;

    public string Mode
    {
        get
        {
            var mounted = Volatile.Read(ref _mountedBackend);
            if (mounted is not null) return $"{Describe(mounted)} (gekoppeld)";
            return SmbReachable && Smb is not null
                ? "LAN-SMB"
                : _remoteIsAuthenticated() ? "UGREENlink" : "UGREENlink (aanmelding vereist)";
        }
    }

    public IFileBackend Current => Volatile.Read(ref _mountedBackend) ?? PreferredBackend();

    public IFileBackend PinForMount()
    {
        var selected = PreferredBackend();
        if (Interlocked.CompareExchange(ref _mountedBackend, selected, null) is not null)
            throw new InvalidOperationException("Er is al een backend vastgezet voor een actieve schijfkoppeling.");
        return selected;
    }

    public void ReleaseMount(IFileBackend? expectedBackend = null)
    {
        if (expectedBackend is null)
        {
            Interlocked.Exchange(ref _mountedBackend, null);
            return;
        }

        Interlocked.CompareExchange(ref _mountedBackend, null, expectedBackend);
    }

    public string Describe(IFileBackend backend) =>
        ReferenceEquals(backend, Smb) ? "LAN-SMB" : ReferenceEquals(backend, Remote) ? "UGREENlink" : "onbekend";

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
            var reachable = await Task.Run(_smbAvailabilityProbe).WaitAsync(TimeSpan.FromSeconds(2));
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

    private IFileBackend PreferredBackend() => SmbReachable && Smb is not null ? Smb : Remote;
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
}
