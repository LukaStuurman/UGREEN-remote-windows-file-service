using DokanNet;
using DokanNet.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UGREENRemoteDrive;

namespace UGREENRemoteDrive.Tests;

[TestClass]
public sealed class HandleSaveTests
{
    private static MockDokanFileInfo Open(DriveFileSystem fs, string path, FileMode mode = FileMode.CreateNew)
    {
        var info = new MockDokanFileInfo();
        Assert.AreEqual(DokanResult.Success, fs.CreateFile(path, DokanNet.FileAccess.GenericAll,
            FileShare.ReadWrite | FileShare.Delete, mode, FileOptions.None, FileAttributes.Normal, info));
        return info;
    }

    [TestMethod]
    public void RandomSaveReplaceRetainsFileIdentityThroughCleanupAndPagingIo()
    {
        var backend = new HandleFixture();
        var fs = new DriveFileSystem(backend);
        var old = Open(fs, "\\drawing.dwg");
        var next = Open(fs, "\\save.tmp");
        Assert.AreEqual(DokanResult.Success, fs.WriteFile("\\drawing.dwg", [1, 2, 3], out _, 0, old));
        var bytes = Enumerable.Range(0, 5 * 1024 * 1024 + 71).Select(i => (byte)(i * 37 % 251)).ToArray();
        Assert.AreEqual(DokanResult.Success, fs.SetEndOfFile("\\save.tmp", bytes.Length, next));
        Assert.AreEqual(DokanResult.Success, fs.WriteFile("\\save.tmp", bytes[1048576..], out _, 1048576, next));
        Assert.AreEqual(DokanResult.Success, fs.WriteFile("\\save.tmp", bytes[..1048576], out _, 0, next));
        Assert.AreEqual(DokanResult.Success, fs.FlushFileBuffers("\\save.tmp", next));
        Assert.AreEqual(DokanResult.Success, fs.MoveFile("\\save.tmp", "\\drawing.dwg", true, next));
        Assert.AreEqual(DokanResult.Success, fs.DeleteFile("\\drawing.dwg", old));
        fs.Cleanup("\\drawing.dwg", old);
        // Dokany can deliver paging I/O after Cleanup. It must still use the old
        // object, even when the supplied pathname now belongs to another file.
        old.PagingIo = true;
        Assert.AreEqual(DokanResult.Success, fs.WriteFile("\\drawing.dwg", [9], out _, 0, old));
        fs.CloseFile("\\drawing.dwg", old);
        Assert.IsNull(old.Context);
        CollectionAssert.AreEqual(bytes, backend.Bytes("\\drawing.dwg"));
        Assert.AreEqual(DokanResult.Success, fs.GetFileInformation("\\save.tmp", out var metadata, next));
        Assert.AreEqual(bytes.Length, metadata.Length);
        fs.CloseFile("\\save.tmp", next);
        Assert.AreEqual(2, backend.Closed);
    }

    [TestMethod]
    public void PagingWritesClipToEofAndAllocationDoesNotExtendLogicalSize()
    {
        var backend = new HandleFixture();
        var fs = new DriveFileSystem(backend);
        var info = Open(fs, "\\drawing.dwg");
        Assert.AreEqual(DokanResult.Success, fs.SetEndOfFile("unused", 5, info));
        Assert.AreEqual(DokanResult.Success, fs.SetAllocationSize("unused", 4096, info));
        info.PagingIo = true;
        Assert.AreEqual(DokanResult.Success, fs.WriteFile("unused", [1, 2, 3, 4], out var written, 3, info));
        Assert.AreEqual(2, written);
        Assert.AreEqual(DokanResult.Success, fs.WriteFile("unused", [9], out written, 5, info));
        Assert.AreEqual(0, written);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 1, 2 }, backend.Bytes("\\drawing.dwg"));
        Assert.AreEqual(DokanResult.Success, fs.SetAllocationSize("unused", 2, info));
        Assert.AreEqual(2, backend.Bytes("\\drawing.dwg").Length);
        fs.CloseFile("unused", info);
    }

    [TestMethod]
    public void FailedDurabilityBarrierAndMissingHandleCannotReportSuccess()
    {
        var backend = new HandleFixture();
        var fs = new DriveFileSystem(backend);
        var info = Open(fs, "\\drawing.dwg");
        backend.FailFlush = true;
        Assert.AreEqual(DokanResult.Error, fs.FlushFileBuffers("unused", info));
        Assert.AreEqual(DokanResult.Error, fs.WriteFile("unused", [1], out var written, 0, new MockDokanFileInfo()));
        Assert.AreEqual(0, written);
        fs.CloseFile("unused", info);
    }

    [TestMethod]
    public void AccessSharingAndCreateFlagsArePreservedAndSharingFailureIsReturned()
    {
        var backend = new HandleFixture();
        var fs = new DriveFileSystem(backend);
        var info = new MockDokanFileInfo();
        var access = DokanNet.FileAccess.GenericRead | DokanNet.FileAccess.Delete;
        Assert.AreEqual(DokanResult.Success, fs.CreateFile("\\flags", access, FileShare.Read,
            FileMode.CreateNew, FileOptions.WriteThrough, FileAttributes.Hidden, info));
        Assert.AreEqual((access, FileShare.Read, FileMode.CreateNew, FileOptions.WriteThrough, FileAttributes.Hidden), backend.Flags);
        fs.CloseFile("\\flags", info);
        backend.FailOpen = true;
        Assert.AreEqual(DokanResult.SharingViolation, fs.CreateFile("\\flags", access, FileShare.Read,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, new MockDokanFileInfo()));
    }

    [TestMethod]
    public void NativeMountedSaveRoundTripOnDisposableRunner()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("UGREEN_SMOKE_DOKAN") != "true")
            Assert.Inconclusive("Native mount runs only on a disposable CI runner after the installer test.");
        var backend = new HandleFixture();
        var logger = new NullLogger();
        using var dokan = new Dokan(logger);
        var root = Enumerable.Range('R', 9).Select(c => $"{(char)c}:\\")
            .First(p => !DriveInfo.GetDrives().Any(d => d.Name == p));
        using var mount = new DokanInstanceBuilder(dokan).ConfigureLogger(() => logger)
            .ConfigureOptions(o => { o.Options = DokanOptions.FixedDrive; o.MountPoint = root; })
            .Build(new DriveFileSystem(backend));
        try
        {
            Assert.IsTrue(SpinWait.SpinUntil(() => Directory.Exists(root), TimeSpan.FromSeconds(10)), "Fixture did not mount.");
            var path = Path.Combine(root, "drawing.dwg");
            var temporary = Path.Combine(root, "drawing.tmp");
            var backup = Path.Combine(root, "drawing.bak");
            File.WriteAllBytes(path, [1, 2, 3]);
            var expected = Enumerable.Range(0, 6 * 1024 * 1024 + 37).Select(i => (byte)(i % 251)).ToArray();
            using (var file = new FileStream(temporary, FileMode.CreateNew, System.IO.FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess | FileOptions.WriteThrough))
            {
                file.SetLength(expected.Length + 8192);
                file.Position = 1048576;
                file.Write(expected.AsSpan(1048576));
                file.Position = 0;
                file.Write(expected.AsSpan(0, 1048576));
                file.SetLength(expected.Length);
                file.Position = 13;
                file.Write([0xf1, 0xff, 0x00]);
                expected[13] = 0xf1; expected[14] = 0xff; expected[15] = 0;
                file.Flush(flushToDisk: true);
            }
            File.Move(path, backup);
            File.Move(temporary, path, overwrite: true);
            CollectionAssert.AreEqual(expected, File.ReadAllBytes(path), "Native saved bytes changed.");
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(backup));
            // Exercise atomic replace-existing with the old target still open.
            using (var old = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                File.WriteAllBytes(temporary, [4, 5, 6, 7]);
                File.Move(temporary, path, overwrite: true);
                var retained = new byte[expected.Length];
                old.ReadExactly(retained);
                CollectionAssert.AreEqual(expected, retained);
            }
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6, 7 }, File.ReadAllBytes(path));
            File.Delete(path); File.Delete(backup);
        }
        finally { dokan.RemoveMountPoint(root); }
    }
}

// Independent file objects survive pathname reuse. Path-based mutation methods
// deliberately throw, catching any accidental fallback by the Dokan adapter.
internal sealed class HandleFixture : IFileBackend, IHandleFileBackend
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Node> _files = new(StringComparer.OrdinalIgnoreCase);
    public bool FailFlush, FailOpen;
    public int Closed;
    public (DokanNet.FileAccess, FileShare, FileMode, FileOptions, FileAttributes) Flags;
    private sealed class Node { public byte[] Data = []; public bool Directory; public DateTimeOffset Modified = DateTimeOffset.UnixEpoch; }
    private static string Key(string p) => p.Trim('\\', '/').Replace('/', '\\');
    public byte[] Bytes(string p) { lock (_gate) return _files[Key(p)].Data.ToArray(); }
    private static FileEntry Entry(string p, Node n) => new(Path.GetFileName(p), n.Directory, n.Data.Length, DateTimeOffset.UnixEpoch, n.Modified);
    public FileEntry Stat(string p)
    {
        lock (_gate) return Key(p) == "" ? Entry("", new Node { Directory = true }) :
            _files.TryGetValue(Key(p), out var n) ? Entry(p, n) : throw new DriveApiException(404, "not found");
    }
    public IReadOnlyList<FileEntry> List(string p) { lock (_gate) return _files.Select(x => Entry(x.Key, x.Value)).ToArray(); }
    public IBackendFileHandle OpenFile(string p, DokanNet.FileAccess access, FileShare sharing, FileMode mode,
        FileOptions options, FileAttributes attributes, bool directory)
    {
        lock (_gate)
        {
            if (FailOpen) throw new DriveApiException(423, "sharing violation");
            Flags = (access, sharing, mode, options, attributes);
            var key = Key(p);
            var existed = _files.TryGetValue(key, out var node);
            if (key == "") return new Handle(this, key, new Node { Directory = true }, true);
            if (existed && mode == FileMode.CreateNew) throw new DriveApiException(409, "exists");
            if (!existed && mode is FileMode.Open or FileMode.Truncate) throw new DriveApiException(404, "not found");
            node ??= new Node { Directory = directory };
            if (mode is FileMode.Create or FileMode.Truncate) node.Data = [];
            _files[key] = node;
            return new Handle(this, key, node, existed);
        }
    }
    private sealed class Handle(HandleFixture owner, string key, Node node, bool existed) : IBackendFileHandle
    {
        private bool _closed, _delete;
        public bool Existed => existed;
        public FileEntry Stat() { lock (owner._gate) return Entry(key, node); }
        public byte[] Read(long offset, int length) { lock (owner._gate) return offset >= node.Data.Length ? [] : node.Data.AsSpan((int)offset, Math.Min(length, node.Data.Length - (int)offset)).ToArray(); }
        public int Write(long offset, byte[] bytes)
        {
            lock (owner._gate)
            {
                if (offset == -1) offset = node.Data.Length;
                if (offset + bytes.Length > node.Data.Length) Array.Resize(ref node.Data, checked((int)offset + bytes.Length));
                bytes.CopyTo(node.Data, (int)offset);
                return bytes.Length;
            }
        }
        public void Flush() { if (owner.FailFlush) throw new IOException("fixture flush failure"); }
        public void Resize(long n) { lock (owner._gate) Array.Resize(ref node.Data, checked((int)n)); }
        public void Reserve(long n) { lock (owner._gate) if (n < node.Data.Length) Resize(n); }
        public void Rename(string target, bool replace)
        {
            lock (owner._gate)
            {
                target = Key(target);
                if (!replace && owner._files.ContainsKey(target)) throw new DriveApiException(409, "exists");
                if (owner._files.TryGetValue(key, out var old) && ReferenceEquals(old, node)) owner._files.Remove(key);
                owner._files[target] = node; key = target;
            }
        }
        public void Delete() { _delete = true; }
        public void SetModifiedTime(DateTimeOffset time) { lock (owner._gate) node.Modified = time; }
        public void SetAttributes(FileAttributes attributes) { }
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_closed) return;
                if (_delete && owner._files.TryGetValue(key, out var current) && ReferenceEquals(current, node)) owner._files.Remove(key);
                _closed = true; owner.Closed++;
            }
        }
    }
    public DiskSpace GetSpace() => new(1L << 30, 2L << 30, 1L << 30);
    public byte[] Read(string p, long offset, int length) => throw new NotSupportedException();
    public int Write(string p, long offset, byte[] data) => throw new NotSupportedException();
    public void CreateFile(string p) => throw new NotSupportedException();
    public void CreateDirectory(string p) => throw new NotSupportedException();
    public void Resize(string p, long length) => throw new NotSupportedException();
    public void Rename(string source, string target, bool replace) => throw new NotSupportedException();
    public void Delete(string p, bool directory) => throw new NotSupportedException();
    public void SetModifiedTime(string p, DateTimeOffset time) => throw new NotSupportedException();
}
