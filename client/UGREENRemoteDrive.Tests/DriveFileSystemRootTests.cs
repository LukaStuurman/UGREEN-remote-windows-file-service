using System.IO;
using DokanNet;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UGREENRemoteDrive;

namespace UGREENRemoteDrive.Tests;

[TestClass]
public sealed class DriveFileSystemRootTests
{
    [TestMethod]
    public void ExistingRootDirectoryIsMarkedAsDirectoryAndOpensReadOnly()
    {
        var backend = new FakeBackend(DirectoryEntry(string.Empty));
        var fileSystem = new DriveFileSystem(backend);

        foreach (var rootPath in new[] { string.Empty, "\\", "/" })
        {
            var info = new MockDokanFileInfo { IsDirectory = false };
            var status = fileSystem.CreateFile(rootPath, default, FileShare.Read, FileMode.Open,
                FileOptions.None, FileAttributes.Directory, info);

            Assert.AreEqual(DokanResult.Success, status, $"Root path form {rootPath.Length} failed.");
            Assert.IsTrue(info.IsDirectory, "Dokan must be told that the opened root is a directory.");
        }

        Assert.AreEqual(0, backend.CreateFileCalls);
        Assert.AreEqual(0, backend.CreateDirectoryCalls);
        Assert.AreEqual(0, backend.ResizeCalls);
    }

    [TestMethod]
    public void MissingRootNeverCreatesAnythingEvenForCreateMode()
    {
        var backend = new FakeBackend(root: null);
        var fileSystem = new DriveFileSystem(backend);
        var info = new MockDokanFileInfo { IsDirectory = true };

        var status = fileSystem.CreateFile("\\", default, FileShare.Read, FileMode.Create,
            FileOptions.None, FileAttributes.Directory, info);

        Assert.AreEqual(DokanResult.PathNotFound, status);
        Assert.AreEqual(0, backend.CreateFileCalls);
        Assert.AreEqual(0, backend.CreateDirectoryCalls);
    }

    [TestMethod]
    public void DirectoryRequestForAFileStillReturnsNotADirectory()
    {
        var backend = new FakeBackend(
            DirectoryEntry(string.Empty),
            new FileEntry("child", false, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        var fileSystem = new DriveFileSystem(backend);
        var info = new MockDokanFileInfo { IsDirectory = true };

        var status = fileSystem.CreateFile("\\child", default, FileShare.Read, FileMode.Open,
            FileOptions.None, FileAttributes.Directory, info);

        Assert.AreEqual(DokanResult.NotADirectory, status);
        Assert.AreEqual(0, backend.CreateFileCalls);
        Assert.AreEqual(0, backend.CreateDirectoryCalls);
    }

    private static FileEntry DirectoryEntry(string name) =>
        new(name, true, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private sealed class FakeBackend(FileEntry? root, FileEntry? child = null) : IFileBackend
    {
        public int CreateFileCalls { get; private set; }
        public int CreateDirectoryCalls { get; private set; }
        public int ResizeCalls { get; private set; }

        public FileEntry Stat(string path)
        {
            var relative = path.TrimStart('\\', '/').Replace('\\', '/');
            if (relative.Length == 0) return root ?? throw new DriveApiException(404, "not found");
            if (relative == "child") return child ?? throw new DriveApiException(404, "not found");
            throw new DriveApiException(404, "not found");
        }

        public IReadOnlyList<FileEntry> List(string path) => Array.Empty<FileEntry>();
        public byte[] Read(string path, long offset, int length) => throw new NotSupportedException();
        public int Write(string path, long offset, byte[] data) => throw new NotSupportedException();
        public void CreateFile(string path) => CreateFileCalls++;
        public void CreateDirectory(string path) => CreateDirectoryCalls++;
        public void Resize(string path, long length) => ResizeCalls++;
        public void Rename(string source, string target, bool replace) => throw new NotSupportedException();
        public void Delete(string path, bool directory) => throw new NotSupportedException();
        public void SetModifiedTime(string path, DateTimeOffset modifiedUtc) => throw new NotSupportedException();
        public DiskSpace GetSpace() => new(0, 0, 0);
    }
}
