using Microsoft.VisualStudio.TestTools.UnitTesting;
using UGREENRemoteDrive.Native;

namespace UGREENRemoteDrive.Native.Tests;

[TestClass]
public class ShareValidationTests
{
    private const string Json = """
      {"TUN":true,"BackendState":"Running","Peer":{
        "peer1":{"DNSName":"nas.example.ts.net.","Online":true,"TailscaleIPs":["100.90.10.20","fd7a:115c:a1e0::1"]},
        "peer2":{"DNSName":"other.example.ts.net.","Online":true,"TailscaleIPs":["100.100.22.7"]}
      }}
      """;

    [TestMethod]
    public void AcceptOnlyTechbaseRoot()
    {
        Assert.AreEqual(@"\\nas\Techbase", NativeSmb.ValidateLanShare(@"\\nas\Techbase"));
        foreach (var value in new[] { @"\\nas\Documents", @"\\nas\Techbase\Other", @"\\nas\Techbase\..\Private",
            @"\\nas", @"https://nas/Techbase", @"\\nas/Techbase" })
            Assert.ThrowsExactly<ArgumentException>(() => NativeSmb.ValidateLanShare(value));
    }

    [TestMethod]
    public void PeerResolutionDoesNotFallbackToPublicDns()
    {
        Assert.AreEqual(@"\\100.90.10.20\Techbase", NativeSmb.ResolveTailnetShare("nas.example.ts.net", Json));
        Assert.AreEqual(@"\\100.90.10.20\Techbase", NativeSmb.ResolveTailnetShare("100.90.10.20", Json));
        foreach (var value in new[] { "nas.example.ts.net.evil", "other.example.com", "192.168.1.2",
            "100.63.1.1", "100.128.0.1", @"\\nas.example.ts.net\Techbase" })
            Assert.ThrowsExactly<ArgumentException>(() => NativeSmb.ResolveTailnetShare(value, Json));
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeSmb.ResolveTailnetShare("nothere.example.ts.net", Json));
    }

    [TestMethod]
    public void RefuseUserspaceOnlyAndUnregisteredPeers()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            NativeSmb.ResolveTailnetShare("nas.example.ts.net", Json.Replace("\"TUN\":true", "\"TUN\":false")));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            NativeSmb.ResolveTailnetShare("nas.example.ts.net", Json.Replace("\"Running\"", "\"Stopped\"")));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            NativeSmb.ResolveTailnetShare("nas.example.ts.net", Json.Replace("\"Online\":true", "\"Online\":false")));
    }

    [TestMethod]
    public void RejectUnsafeLetters()
    {
        Assert.AreEqual("T:", NativeSmb.ValidateDrive("t:"));
        foreach (var value in new[] { "C:", "T:\\", "T:\\Techbase", "AA:", "" })
            Assert.ThrowsExactly<ArgumentException>(() => NativeSmb.ValidateDrive(value));
    }
}
