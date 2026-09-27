using Microsoft.VisualStudio.TestTools.UnitTesting;
using UGREENRemoteDrive;

namespace UGREENRemoteDrive.Tests;

[TestClass]
public sealed class UgreenLinkSetupAddressTests
{
    [TestMethod]
    public void AcceptsNasPortalUrlAsAutomaticShortcutDiscoveryEntry()
    {
        var target = UgreenLinkSetupAddress.Parse("https://ug.link/TechbaseNAS");

        Assert.AreEqual(UgreenLinkSetupKind.NasPortal, target.Kind);
        Assert.AreEqual("https://ug.link/TechbaseNAS", target.Address.AbsoluteUri);
    }

    [TestMethod]
    public void ContinuesToAcceptDirectDockerShortcutAddress()
    {
        var target = UgreenLinkSetupAddress.Parse("https://remote.ugdocker.link/api/ugreen/auth?ticket=temporary");

        Assert.AreEqual(UgreenLinkSetupKind.Shortcut, target.Kind);
        Assert.AreEqual("remote.ugdocker.link", target.Address.Host);
        Assert.AreEqual("/", target.Address.AbsolutePath);
        Assert.AreEqual("", target.Address.Query);
    }

    [TestMethod]
    public void RejectsUnsafeOrAmbiguousNasPortalAddresses()
    {
        foreach (var value in new[]
        {
            "http://ug.link/TechbaseNAS",
            "https://ug.link/",
            "https://ug.link/id/extra",
            "https://ug.link/id?ticket=secret",
            "https://ug.link/id#fragment",
            "https://ug.link:8443/id",
            "https://user@ug.link/id",
            "https://ug.link.example.com/id",
            "https://example.com/id"
        })
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => UgreenLinkSetupAddress.Parse(value), value);
        }
    }

    [TestMethod]
    public void ShortcutDiscoveryOnlyAcceptsExactTrustedTileTargets()
    {
        const string desktop = "https://nas.eur3.ug.link/desktop/";
        const string desktopFrame = "https://nas.eur3.ug.link/desktop/#/apps";

        Assert.IsTrue(RemoteBridge.IsAllowedDiscoveredShortcutRootPopupTarget(
            desktopFrame, desktop, new Uri("https://remote.ugdocker.link/")));
        Assert.IsTrue(RemoteBridge.IsAllowedDiscoveredAuthBootstrapPopupTarget(
            desktopFrame, desktop, new Uri("https://remote.ugdocker.link/api/ugreen/auth?ticket=one-time")));

        Assert.IsFalse(RemoteBridge.IsAllowedDiscoveredShortcutRootPopupTarget(
            desktopFrame, desktop, new Uri("https://remote.ugdocker.link/other")));
        Assert.IsFalse(RemoteBridge.IsAllowedDiscoveredAuthBootstrapPopupTarget(
            desktopFrame, desktop, new Uri("https://remote.ugdocker.link/api/ugreen/auth")));
        Assert.IsFalse(RemoteBridge.IsAllowedDiscoveredShortcutRootPopupTarget(
            "https://attacker.example/desktop/", "https://attacker.example/desktop/",
            new Uri("https://remote.ugdocker.link/")));
        Assert.IsFalse(RemoteBridge.IsAllowedDiscoveredShortcutRootPopupTarget(
            desktopFrame, desktop, new Uri("https://remote.ugdocker.link.example.com/")));
    }

    [TestMethod]
    public void AutomaticTilePopupRequiresTrustedDesktopAndExactConfiguredShortcutTarget()
    {
        const string desktop = "https://nas.eur3.ug.link/desktop/";
        var desktopOrigin = new Uri("https://nas.eur3.ug.link/");
        var configuredRoot = new Uri("https://remote.ugdocker.link/");

        Assert.IsTrue(RemoteBridge.IsAllowedAutomaticTilePopupTarget(
            desktop, desktop, new Uri("https://remote.ugdocker.link/"), desktopOrigin, configuredRoot));
        Assert.IsTrue(RemoteBridge.IsAllowedAutomaticTilePopupTarget(
            desktop, desktop, new Uri("https://remote.ugdocker.link/api/ugreen/auth?ticket=one-time"), desktopOrigin, configuredRoot));
        Assert.IsFalse(RemoteBridge.IsAllowedAutomaticTilePopupTarget(
            desktop, desktop, new Uri("https://other.ugdocker.link/"), desktopOrigin, configuredRoot));
        Assert.IsFalse(RemoteBridge.IsAllowedAutomaticTilePopupTarget(
            desktop, desktop, new Uri("https://remote.ugdocker.link/api/v1/files"), desktopOrigin, configuredRoot));
        Assert.IsFalse(RemoteBridge.IsAllowedAutomaticTilePopupTarget(
            desktop, desktop, new Uri("http://remote.ugdocker.link/"), desktopOrigin, configuredRoot));
        Assert.IsFalse(RemoteBridge.IsAllowedAutomaticTilePopupTarget(
            "https://other.eur3.ug.link/desktop/", desktop,
            new Uri("https://remote.ugdocker.link/"), desktopOrigin, configuredRoot));
        Assert.IsFalse(RemoteBridge.IsAllowedAutomaticTilePopupTarget(
            desktop, desktop, new Uri("https://remote.ugdocker.link/"),
            new Uri("https://other.eur3.ug.link/"), configuredRoot));
    }

    [TestMethod]
    public void NasPortalNavigationIsLimitedToHttpsUgreenDomains()
    {
        Assert.IsTrue(RemoteBridge.IsUgreenLinkPortalNavigation(new Uri("https://ug.link/TechbaseNAS")));
        Assert.IsTrue(RemoteBridge.IsUgreenLinkPortalNavigation(new Uri("https://nas.eur3.ug.link/desktop/")));
        Assert.IsFalse(RemoteBridge.IsUgreenLinkPortalNavigation(new Uri("http://ug.link/TechbaseNAS")));
        Assert.IsFalse(RemoteBridge.IsUgreenLinkPortalNavigation(new Uri("https://ug.link.evil.example/desktop/")));
        Assert.IsFalse(RemoteBridge.IsUgreenLinkPortalNavigation(new Uri("https://user@ug.link/TechbaseNAS")));
        Assert.IsFalse(RemoteBridge.IsUgreenLinkPortalNavigation(new Uri("https://ug.link:8443/TechbaseNAS")));
        Assert.IsFalse(RemoteBridge.IsUgreenLinkPortalNavigation(new Uri("https://ug.link/TechbaseNAS#external")));
    }
}
