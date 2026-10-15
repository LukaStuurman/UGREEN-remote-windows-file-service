using Microsoft.VisualStudio.TestTools.UnitTesting;
using UGREENRemoteDrive;

namespace UGREENRemoteDrive.Tests;

[TestClass]
public sealed class MetadataCoalescingTests
{
    private static FileEntry Entry(string name, long size = 1) => new(name, false, size, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [TestMethod]
    public async Task ConcurrentStatAndListRequestsEachUseOneLoader()
    {
        var cache = new DriveMetadataCache(TimeSpan.FromSeconds(10));
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var statCalls = 0;
        var listCalls = 0;
        FileEntry Stat()
        {
            Interlocked.Increment(ref statCalls);
            entered.Signal();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return Entry("fixture");
        }
        IReadOnlyList<FileEntry> List()
        {
            Interlocked.Increment(ref listCalls);
            entered.Signal();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return [Entry("child")];
        }
        var firstStat = Task.Run(() => cache.GetStat("folder/fixture", Stat));
        var firstList = Task.Run(() => cache.GetList("folder", List));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(3)));
        var duplicateStat = Task.Run(() => cache.GetStat("folder\\fixture", Stat));
        var duplicateList = Task.Run(() => cache.GetList("folder/", List));
        release.Set();
        await Task.WhenAll(firstStat, firstList, duplicateStat, duplicateList);
        Assert.AreEqual(1, statCalls);
        Assert.AreEqual(1, listCalls);
        Assert.AreEqual("child", cache.GetStat("folder/child", () => throw new AssertFailedException()).Name);
    }

    [TestMethod]
    public async Task InvalidatingInflightMetadataStartsAFreshRequestAndRejectsOldCacheResults()
    {
        var cache = new DriveMetadataCache(TimeSpan.FromSeconds(10));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var old = Task.Run(() => cache.GetStat("fixture", () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return Entry("fixture", 1);
        }));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(3)));
        cache.Invalidate();
        Assert.AreEqual(2L, cache.GetStat("fixture", () => Entry("fixture", 2)).Size);
        release.Set();
        await old;
        Assert.AreEqual(2L, cache.GetStat("fixture", () => throw new AssertFailedException()).Size);
    }

    [TestMethod]
    public void FailuresAreNotCachedOrRetriedAsWrites()
    {
        var cache = new DriveMetadataCache(TimeSpan.FromSeconds(10));
        Assert.ThrowsExactly<IOException>(() => cache.GetStat("fixture", () => throw new IOException()));
        Assert.AreEqual(2L, cache.GetStat("fixture", () => Entry("fixture", 2)).Size);
        Assert.ThrowsExactly<IOException>(() => cache.GetList("folder", () => throw new IOException()));
        Assert.AreEqual(1, cache.GetList("folder", () => [Entry("child")]).Count);
    }
}
