using Microsoft.VisualStudio.TestTools.UnitTesting;
using UGREENRemoteDrive;

namespace UGREENRemoteDrive.Tests;

[TestClass]
public sealed class EmbeddedTailscaleTests
{
    [TestMethod]
    public void OnlyTailscaleAddressesAreAcceptedWithoutNetworkLookup()
    {
        foreach (var host in new[] { "fixture.example.ts.net", "100.64.0.1", "100.127.255.254" })
            Assert.AreEqual(host, EmbeddedTailscaleBackend.ValidateHost(host));
        foreach (var host in new[] { "100.63.255.255", "100.128.0.1", "192.0.2.1", "127.0.0.1", "fixture.example.ts.net.evil", "https://fixture.example.ts.net", @"\\fixture.example.ts.net\Techbase", "bad_thing.ts.net" })
            Assert.ThrowsExactly<ArgumentException>(() => EmbeddedTailscaleBackend.ValidateHost(host));
    }

    [TestMethod]
    public async Task PrivateHelperKeepsConcurrentRepliesAndBinaryFramesAlignedWithoutStartingTailscale()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            Assert.Inconclusive("Native helper startup is limited to a disposable GitHub runner.");
        using var helper = new TailscaleBroker(); // No configure: no tailnet or profile is opened.
        var replies = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => helper.Call("status"))));
        Assert.AreEqual(32, replies.Select(reply => reply.Id).Distinct().Count());
        foreach (var reply in replies)
        {
            Assert.AreEqual(200, reply.Status);
            Assert.AreEqual("NotStarted", reply.Value.GetProperty("state").GetString());
            Assert.IsFalse(reply.Value.TryGetProperty("authUrl", out _));
        }
        var data = Enumerable.Range(0, 4 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
        Assert.AreEqual(403, await Task.Run(() => helper.Call("write", new { path = "fixture" }, data).Status));
        Assert.AreEqual(200, helper.Call("status").Status);
        helper.Dispose();
        Assert.ThrowsExactly<DriveDisconnectedException>(() => helper.Call("status"));
    }
}
