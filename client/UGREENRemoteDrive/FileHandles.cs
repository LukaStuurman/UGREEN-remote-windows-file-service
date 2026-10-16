using DokanNet;
using System.Text.Json;

namespace UGREENRemoteDrive;

// A handle is the opened file object, not a path. In particular it must not
// silently switch to a replacement file when another handle renames/deletes it.
internal interface IHandleFileBackend
{
    IBackendFileHandle OpenFile(string path, DokanNet.FileAccess access, FileShare sharing,
        FileMode mode, FileOptions options, FileAttributes attributes, bool directory);
}

internal interface IBackendFileHandle : IDisposable
{
    bool Existed { get; }
    FileEntry Stat();
    byte[] Read(long offset, int length);
    int Write(long offset, byte[] data);
    void Flush();
    void Resize(long length);
    void Reserve(long length);
    void Rename(string target, bool replace);
    void Delete();
    void SetModifiedTime(DateTimeOffset modifiedUtc);
    void SetAttributes(FileAttributes attributes);
}

internal sealed class EmbeddedFileHandle(EmbeddedTailscaleBackend backend, ulong id, bool existed) : IBackendFileHandle
{
    private readonly object _gate = new();
    private bool _closed;
    public bool Existed => existed;
    private TailnetReply Call(string op, object? args = null, byte[]? data = null)
    {
        if (_closed) throw new ObjectDisposedException(nameof(EmbeddedFileHandle));
        return backend.HandleCall(op, id, args, data);
    }
    public FileEntry Stat() { lock (_gate) return Call("handle-stat").Value.Deserialize<FileEntry>(JsonDefaults.Options)!; }
    public byte[] Read(long offset, int length)
    {
        lock (_gate)
        {
            // No path cache: different handles can refer to different file IDs
            // at the same pathname during an atomic replacement. Mutation and
            // external writers must not leave stale CAD section/header bytes.
            return Call("handle-read", new { offset, length }).Data;
        }
    }
    private T Mutate<T>(Func<T> action) { lock (_gate) { return action(); } }
    public int Write(long offset, byte[] data) => Mutate(() => Call("handle-write", new { offset }, data).Value.GetProperty("written").GetInt32());
    public void Flush() { lock (_gate) Call("handle-flush"); }
    public void Resize(long length) { lock (_gate) Call("handle-resize", new { size = length }); }
    public void Reserve(long length) { lock (_gate) Call("handle-reserve", new { size = length }); }
    public void Rename(string target, bool replace) { lock (_gate) Call("handle-rename", new { target = target.TrimStart('\\','/').Replace('\\','/'), replace }); }
    public void Delete() { lock (_gate) Call("handle-delete"); }
    public void SetModifiedTime(DateTimeOffset modifiedUtc) { lock (_gate) Call("handle-times", new { modified = modifiedUtc }); }
    public void SetAttributes(FileAttributes attributes) { lock (_gate) Call("handle-attributes", new { attributes = (uint)attributes }); }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            try { Call("handle-close"); }
            finally { _closed = true; }
        }
    }
}
