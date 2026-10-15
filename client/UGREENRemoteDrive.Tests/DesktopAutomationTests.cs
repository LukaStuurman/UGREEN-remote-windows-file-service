using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UGREENRemoteDrive;

namespace UGREENRemoteDrive.Tests;

[TestClass]
public sealed class DesktopAutomationTests
{
    [TestMethod]
    public void OlderSettingsEnableSessionReuseAndAutomaticOpeningByDefault()
    {
        var settings = JsonSerializer.Deserialize<DriveSettings>("{\"DriveLetter\":\"U:\",\"AutoStart\":false}")!;
        Assert.IsTrue(settings.RememberLogin);
        Assert.IsTrue(settings.AutoOpenShortcut);
        Assert.AreEqual("ugreen-remote-drive", settings.ShortcutTileName);
        Assert.AreEqual("", settings.ShortcutTileId);
    }

    [TestMethod]
    public void SavedChoicesAndLearnedTileIdentitySurviveSettingsSerialization()
    {
        var original = new DriveSettings { RememberLogin = false, AutoOpenShortcut = false,
            ShortcutTileName = "My Drive", ShortcutTileId = "shortcut.123" };
        var restored = JsonSerializer.Deserialize<DriveSettings>(JsonSerializer.Serialize(original))!;
        Assert.IsFalse(restored.RememberLogin);
        Assert.IsFalse(restored.AutoOpenShortcut);
        Assert.AreEqual(original.ShortcutTileName, restored.ShortcutTileName);
        Assert.AreEqual(original.ShortcutTileId, restored.ShortcutTileId);
    }

    [TestMethod]
    public void DesktopSelectionRejectsUnboundedOrQueryBearingIdentity()
    {
        Assert.IsNotNull(DesktopTileSelection.Validate("docker.shortcut-1", "ugreen-remote-drive"));
        Assert.IsNull(DesktopTileSelection.Validate("shortcut?ticket=secret", "Remote Drive"));
        Assert.IsNull(DesktopTileSelection.Validate("id", ""));
        Assert.IsNull(DesktopTileSelection.Validate("id", "bad\nname"));
        Assert.IsNull(DesktopTileSelection.Validate(new string('x', 201), "Remote Drive"));
        Assert.IsNull(DesktopTileSelection.Validate("id", new string('x', 129)));
    }

    [TestMethod]
    public void DesktopHelperConfigurationContainsOnlyShortcutOriginAndNoTicket()
    {
        var script = DesktopAutomation.BuildScript(new DriveSettings(),
            new Uri("https://remote.ugdocker.link/api/ugreen/auth?ticket=never-export-this"));
        StringAssert.Contains(script, "https://remote.ugdocker.link");
        Assert.IsFalse(script.Contains("never-export-this", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("__CONFIG__", StringComparison.Ordinal));
    }
}
