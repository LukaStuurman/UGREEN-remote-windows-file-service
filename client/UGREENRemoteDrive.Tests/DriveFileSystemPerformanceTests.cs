using DokanNet;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UGREENRemoteDrive;

namespace UGREENRemoteDrive.Tests;

[TestClass]
public sealed class DriveFileSystemPerformanceTests
{
    [TestMethod]
    public void WriteUsesFourMegabyteChunksAndPreservesOffsets()
    {
        var backend = new PerformanceBackend();
        var fileSystem = new DriveFileSystem(backend);
        var data = new byte[5 * 1024 * 1024];

        var status = fileSystem.WriteFile("\\large.bin", data, out var bytesWritten, 17, new MockDokanFileInfo());

        Assert.AreEqual(DokanResult.Success, status);
        Assert.AreEqual(data.Length, bytesWritten);
        Assert.AreEqual(2, backend.Writes.Count);
        Assert.AreEqual((17L, 4 * 1024 * 1024), backend.Writes[0]);
        Assert.AreEqual((17L + 4 * 1024 * 1024, 1024 * 1024), backend.Writes[1]);
    }

    [TestMethod]
    public void DirectoryListingSuppliesMetadataForImmediateChildLookups()
    {
        var backend = new PerformanceBackend();
        var fileSystem = new DriveFileSystem(backend);

        var listStatus = fileSystem.FindFiles("\\folder", out var files, new MockDokanFileInfo());
        var statStatus = fileSystem.GetFileInformation("\\folder\\notes.txt", out var info, new MockDokanFileInfo());

        Assert.AreEqual(DokanResult.Success, listStatus);
        Assert.AreEqual(DokanResult.Success, statStatus);
        Assert.AreEqual("notes.txt", files.Single().FileName);
        Assert.AreEqual(23L, info.Length);
        Assert.AreEqual(1, backend.ListCalls);
        Assert.AreEqual(0, backend.StatCalls);
    }

    [TestMethod]
    public void SuccessfulWriteInvalidatesCachedFileMetadata()
    {
        var backend = new PerformanceBackend();
        var fileSystem = new DriveFileSystem(backend);

        Assert.AreEqual(DokanResult.Success,
            fileSystem.GetFileInformation("\\notes.txt", out _, new MockDokanFileInfo()));
        Assert.AreEqual(DokanResult.Success,
            fileSystem.WriteFile("\\notes.txt", [1, 2, 3], out _, 0, new MockDokanFileInfo()));
        Assert.AreEqual(DokanResult.Success,
            fileSystem.GetFileInformation("\\notes.txt", out _, new MockDokanFileInfo()));

        Assert.AreEqual(2, backend.StatCalls);
    }

    private sealed class PerformanceBackend : IFileBackend
    {
        private static readonly DateTimeOffset TestTime = DateTimeOffset.UnixEpoch;
        public List<(long Offset, int Length)> Writes { get; } = [];
        public int StatCalls { get; private set; }
        public int ListCalls { get; private set; }

        public FileEntry Stat(string path)
        {
            StatCalls++;
            if (path.Equals("\\notes.txt", StringComparison.OrdinalIgnoreCase))
                return new FileEntry("notes.txt", false, 23, TestTime, TestTime);
            throw new DriveApiException(404, "not found");
        }

        public IReadOnlyList<FileEntry> List(string path)
        {
            ListCalls++;
            return [new FileEntry("notes.txt", false, 23, TestTime, TestTime)];
        }

        public byte[] Read(string path, long offset, int length) => throw new NotSupportedException();

        public int Write(string path, long offset, byte[] data)
        {
            Writes.Add((offset, data.Length));
            return data.Length;
        }

        public void CreateFile(string path) => throw new NotSupportedException();
        public void CreateDirectory(string path) => throw new NotSupportedException();
        public void Resize(string path, long length) => throw new NotSupportedException();
        public void Rename(string source, string target, bool replace) => throw new NotSupportedException();
        public void Delete(string path, bool directory) => throw new NotSupportedException();
        public void SetModifiedTime(string path, DateTimeOffset modifiedUtc) => throw new NotSupportedException();
        public DiskSpace GetSpace() => new(0, 0, 0);
    }
}
