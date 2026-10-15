using Microsoft.VisualStudio.TestTools.UnitTesting;
using UGREENRemoteDrive;

namespace UGREENRemoteDrive.Tests;

[TestClass]
public sealed class DirectConnectionSettingsTests
{
    [TestMethod]
    public void SmbOnlyConnectionNeedsNoUgreenLinkAddressAndKeepsShareRestriction()
    {
        Assert.IsNull(UgreenLinkSetupAddress.ParseConnection("", @"\\10.203.0.1\Techbase"));
        Assert.IsNull(UgreenLinkSetupAddress.ParseConnection(" ", @"\\fixture-nas\Techbase"));
        Assert.ThrowsExactly<InvalidOperationException>(() => UgreenLinkSetupAddress.ParseConnection("", ""));
        Assert.ThrowsExactly<ArgumentException>(() => UgreenLinkSetupAddress.ParseConnection("", @"\\10.203.0.1\OtherShare"));
        Assert.ThrowsExactly<ArgumentException>(() => UgreenLinkSetupAddress.ParseConnection("", @"\\10.203.0.1\Techbase\child"));
        Assert.ThrowsExactly<InvalidOperationException>(() => UgreenLinkSetupAddress.ParseConnection("http://example.com", @"\\10.203.0.1\Techbase"));
    }

    [TestMethod]
    public void ReservedWireGuardShareHasDistinctConnectionName()
    {
        Assert.AreEqual("WireGuard-SMB", new SmbBackend(@"\\10.203.0.1\Techbase").ConnectionName);
        Assert.AreEqual("LAN-SMB", new SmbBackend(@"\\fixture-nas\Techbase").ConnectionName);
    }
}
