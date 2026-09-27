using DokanNet;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UGREENRemoteDrive;

namespace UGREENRemoteDrive.Tests;

[TestClass]
public sealed class BackendPinningTests
{
    [TestMethod]
    public async Task MountedBackendStaysPinnedAcrossSmbChangesAndListsRoot()
    {
        await VerifyMountedBackendStaysPinnedAsync(smbInitiallyReachable: true);
        await VerifyMountedBackendStaysPinnedAsync(smbInitiallyReachable: false);
    }

    private static async Task VerifyMountedBackendStaysPinnedAsync(bool smbInitiallyReachable)
    {
        var remote = new CountingBackend(new DiskSpace(11, 101, 21));
        var smb = new CountingBackend(new DiskSpace(22, 202, 32));
        var smbReachable = smbInitiallyReachable;
        var selector = new BackendSelector(remote, smb, () => true, () => smbReachable);

        await selector.ProbeSmbAsync();
        var expectedBackend = smbInitiallyReachable ? smb : remote;
        var otherBackend = smbInitiallyReachable ? remote : smb;
        var mountedBackend = selector.PinForMount();
        var fileSystem = new DriveFileSystem(mountedBackend);

        Assert.AreSame(expectedBackend, mountedBackend);
        Assert.AreSame(mountedBackend, selector.Current);
        Assert.AreEqual(selector.Describe(mountedBackend) + " (gekoppeld)", selector.Mode);

        var spaceStatus = fileSystem.GetDiskFreeSpace(
            out _, out var totalBytes, out _, new MockDokanFileInfo());
        Assert.AreEqual(DokanResult.Success, spaceStatus);
        Assert.AreEqual(smbInitiallyReachable ? 202L : 101L, totalBytes);

        smbReachable = !smbInitiallyReachable;
        await selector.ProbeSmbAsync();
        Assert.AreEqual(!smbInitiallyReachable, selector.SmbReachable);
        Assert.AreSame(mountedBackend, selector.Current, "The active mount must ignore the new preferred backend.");

        var openInfo = new MockDokanFileInfo { IsDirectory = false };
        var openStatus = fileSystem.CreateFile("\\", default, FileShare.Read, FileMode.Open,
            FileOptions.None, FileAttributes.Directory, openInfo);
        Assert.AreEqual(DokanResult.Success, openStatus);
        Assert.IsTrue(openInfo.IsDirectory);

        var metadataStatus = fileSystem.GetFileInformation("\\", out _, new MockDokanFileInfo());
        Assert.AreEqual(DokanResult.Success, metadataStatus);

        smbReachable = smbInitiallyReachable;
        await selector.ProbeSmbAsync();
        smbReachable = !smbInitiallyReachable;
        await selector.ProbeSmbAsync();
        Assert.AreSame(mountedBackend, selector.Current);

        var listingStatus = fileSystem.FindFiles("\\", out var entries, new MockDokanFileInfo());
        Assert.AreEqual(DokanResult.Success, listingStatus);
        Assert.AreEqual(1, entries.Count);
        Assert.IsTrue(entries[0].Attributes.HasFlag(FileAttributes.Directory));

        Assert.AreEqual(1, expectedBackend.GetSpaceCalls);
        Assert.AreEqual(1, expectedBackend.StatCalls, "The root open and immediate metadata lookup share a short-lived cache entry.");
        Assert.AreEqual(1, expectedBackend.ListCalls);
        Assert.AreEqual(0, otherBackend.GetSpaceCalls);
        Assert.AreEqual(0, otherBackend.StatCalls);
        Assert.AreEqual(0, otherBackend.ListCalls);

        selector.ReleaseMount(mountedBackend);
        Assert.AreSame(smbReachable ? smb : remote, selector.Current,
            "A newly selected backend may be used after unmount.");
    }

    private sealed class CountingBackend(DiskSpace space) : IFileBackend
    {
        private static readonly DateTimeOffset TestTime = DateTimeOffset.UnixEpoch;
        private static readonly FileEntry Root = new(string.Empty, true, 0, TestTime, TestTime);
        private static readonly IReadOnlyList<FileEntry> RootEntries =
            [new FileEntry("fixture-directory", true, 0, TestTime, TestTime)];

        public int GetSpaceCalls { get; private set; }
        public int StatCalls { get; private set; }
        public int ListCalls { get; private set; }

        public FileEntry Stat(string path)
        {
            StatCalls++;
            if (string.IsNullOrEmpty(path) || path.Trim('\\', '/').Length == 0) return Root;
            throw new DriveApiException(404, "not found");
        }

        public IReadOnlyList<FileEntry> List(string path)
        {
            ListCalls++;
            return RootEntries;
        }

        public byte[] Read(string path, long offset, int length) => throw new NotSupportedException();
        public int Write(string path, long offset, byte[] data) => throw new NotSupportedException();
        public void CreateFile(string path) => throw new NotSupportedException();
        public void CreateDirectory(string path) => throw new NotSupportedException();
        public void Resize(string path, long length) => throw new NotSupportedException();
        public void Rename(string source, string target, bool replace) => throw new NotSupportedException();
        public void Delete(string path, bool directory) => throw new NotSupportedException();
        public void SetModifiedTime(string path, DateTimeOffset modifiedUtc) => throw new NotSupportedException();

        public DiskSpace GetSpace()
        {
            GetSpaceCalls++;
            return space;
        }
    }
}
