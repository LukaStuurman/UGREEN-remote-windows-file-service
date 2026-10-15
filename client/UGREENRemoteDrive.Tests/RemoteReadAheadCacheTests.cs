using Microsoft.VisualStudio.TestTools.UnitTesting;
using UGREENRemoteDrive;

namespace UGREENRemoteDrive.Tests;

[TestClass]
public sealed class RemoteReadAheadCacheTests
{
    [TestMethod]
    public void SequentialOneMiBCopyNeedsTwoRequestsInsteadOfSixteen()
    {
        var cache = new RemoteReadAheadCache(() => 10);
        var source = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
        var requests = new List<(long Offset, int Length)>();
        byte[] Fetch(string path, long offset, int length)
        {
            requests.Add((offset, length));
            return source.AsSpan((int)offset, Math.Min(length, source.Length - (int)offset)).ToArray();
        }
        for (var offset = 0; offset < source.Length; offset += 64 * 1024)
        {
            var result = cache.Read("fixture.bin", offset, 64 * 1024, 1, Fetch, () => true);
            CollectionAssert.AreEqual(source.AsSpan(offset, 64 * 1024).ToArray(), result);
        }
        Assert.AreEqual(2, requests.Count);
        Assert.AreEqual((0L, 64 * 1024), requests[0]);
        Assert.AreEqual((64 * 1024L, 1024 * 1024), requests[1]);
    }

    [TestMethod]
    public void FirstAndRandomSmallReadsAreNotExpanded()
    {
        var cache = new RemoteReadAheadCache(() => 10);
        var lengths = new List<int>();
        byte[] Fetch(string path, long offset, int length) { lengths.Add(length); return new byte[length]; }
        cache.Read("fixture.bin", 0, 4096, 1, Fetch, () => true);
        cache.Read("fixture.bin", 1024 * 1024, 4096, 1, Fetch, () => true);
        CollectionAssert.AreEqual(new[] { 4096, 4096 }, lengths);
    }

    [TestMethod]
    public void ExpiryRefreshesExternalChangesAndReturnedArraysDoNotModifyCache()
    {
        long now = 10;
        byte value = 1;
        var calls = 0;
        var cache = new RemoteReadAheadCache(() => now);
        byte[] Fetch(string path, long offset, int length) { calls++; return [value]; }
        var first = cache.Read("fixture", 0, 1, 1, Fetch, () => true);
        first[0] = 99;
        Assert.AreEqual((byte)1, cache.Read("fixture", 0, 1, 1, Fetch, () => true)[0]);
        Assert.AreEqual(1, calls);
        value = 2;
        now += 1001;
        Assert.AreEqual((byte)2, cache.Read("fixture", 0, 1, 1, Fetch, () => true)[0]);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task AnInflightReadCannotRestoreDataAfterMutationInvalidation()
    {
        var cache = new RemoteReadAheadCache(() => 10);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var old = Task.Run(() => cache.Read("fixture", 0, 1, 1, (_, _, _) =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return [1];
        }, () => true));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(3)));
        cache.Invalidate();
        release.Set();
        CollectionAssert.AreEqual(new byte[] { 1 }, await old);
        var calls = 0;
        var fresh = cache.Read("fixture", 0, 1, 1, (_, _, _) => { calls++; return [2]; }, () => true);
        CollectionAssert.AreEqual(new byte[] { 2 }, fresh);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void LogoutAndNavigationNeverServeAnOldReadWindow()
    {
        var cache = new RemoteReadAheadCache(() => 10);
        var calls = 0;
        byte[] Fetch(string path, long offset, int length) { calls++; return [(byte)calls]; }
        cache.Read("fixture", 0, 1, 1, Fetch, () => true);
        Assert.ThrowsExactly<DriveDisconnectedException>(() => cache.Read("fixture", 0, 1, 1, Fetch, () => false));
        var afterNavigation = cache.Read("fixture", 0, 1, 2, Fetch, () => true);
        Assert.AreEqual((byte)2, afterNavigation[0]);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task DuplicateConcurrentReadsShareOneFetch()
    {
        var cache = new RemoteReadAheadCache(() => 10);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        byte[] Fetch(string path, long offset, int length)
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return [7];
        }
        var first = Task.Run(() => cache.Read("fixture", 0, 1, 1, Fetch, () => true));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(3)));
        var second = Task.Run(() => cache.Read("fixture", 0, 1, 1, Fetch, () => true));
        release.Set();
        var results = await Task.WhenAll(first, second);
        Assert.AreEqual(1, calls);
        Assert.IsTrue(results.All(result => result[0] == 7));
    }

    [TestMethod]
    public async Task DifferentReadOffsetsCanUseTheNetworkInParallel()
    {
        var cache = new RemoteReadAheadCache(() => 10);
        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        byte[] Fetch(string path, long offset, int length)
        {
            (offset == 0 ? firstEntered : secondEntered).Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return [(byte)offset];
        }
        var first = Task.Run(() => cache.Read("fixture", 0, 1, 1, Fetch, () => true));
        Assert.IsTrue(firstEntered.Wait(TimeSpan.FromSeconds(3)));
        var second = Task.Run(() => cache.Read("fixture", 10, 1, 1, Fetch, () => true));
        Assert.IsTrue(secondEntered.Wait(TimeSpan.FromSeconds(3)), "Independent reads must not be serialized by the cache.");
        release.Set();
        var results = await Task.WhenAll(first, second);
        Assert.AreEqual((byte)0, results[0][0]);
        Assert.AreEqual((byte)10, results[1][0]);
    }

    [TestMethod]
    public void EndOfFileAndEmptyReadsDoNotGenerateExtraRequests()
    {
        var cache = new RemoteReadAheadCache(() => 10);
        var calls = 0;
        byte[] Fetch(string path, long offset, int length) { calls++; return [1, 2, 3]; }
        Assert.AreEqual(0, cache.Read("fixture", 0, 0, 1, Fetch, () => true).Length);
        Assert.AreEqual(3, cache.Read("fixture", 0, 4096, 1, Fetch, () => true).Length);
        Assert.AreEqual(0, cache.Read("fixture", 3, 4096, 1, Fetch, () => true).Length);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void FailedReadsAreNotCachedAndRemoteCaseSensitivePathsStaySeparate()
    {
        var cache = new RemoteReadAheadCache(() => 10);
        Assert.ThrowsExactly<IOException>(() => cache.Read("A", 0, 1, 1, (_, _, _) => throw new IOException(), () => true));
        CollectionAssert.AreEqual(new byte[] { 1 }, cache.Read("A", 0, 1, 1, (_, _, _) => [1], () => true));
        CollectionAssert.AreEqual(new byte[] { 2 }, cache.Read("a", 0, 1, 1, (_, _, _) => [2], () => true));
    }

    [TestMethod]
    public void OnlyEightFilesAreRetainedAndLargeTransfersRemainWithinApiLimit()
    {
        var cache = new RemoteReadAheadCache(() => 10);
        var calls = 0;
        byte[] Fetch(string path, long offset, int length) { calls++; Assert.IsTrue(length <= 4 * 1024 * 1024); return [1]; }
        for (var index = 0; index < 9; index++) cache.Read("file" + index, 0, 1, 1, Fetch, () => true);
        cache.Read("file0", 0, 1, 1, Fetch, () => true);
        Assert.AreEqual(10, calls);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => cache.Read("file", 0, 4 * 1024 * 1024 + 1, 1, Fetch, () => true));
    }
}
