using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;
using UGREENRemoteDrive;

namespace UGREENRemoteDrive.Tests;

[TestClass]
public sealed class WindowsSmbDriveTests
{
    private const string Root = @"\\fixture-nas\Techbase";

    [TestMethod]
    public void NativeEnrollmentAndNasReadinessRemainSeparateWithoutDnsFallback()
    {
        const string json = """
            {"TUN":true,"BackendState":"Running","Peer":{"p":{"DNSName":"nas.example.ts.net.",
            "Online":true,"TailscaleIPs":["100.90.10.20"]}}}
            """;
        var found = WindowsSmbTailnet.ParseStatus("nas.example.ts.net", json);
        Assert.IsTrue(found.Status.Ready);
        Assert.AreEqual(@"\\100.90.10.20\Techbase", found.PeerRoot);
        foreach (var data in new[] { json.Replace("\"Online\":true", "\"Online\":false"),
            json.Replace("\"TUN\":true", "\"TUN\":false"), json.Replace("nas.example", "another.example") })
        {
            var missing = WindowsSmbTailnet.ParseStatus("nas.example.ts.net", data);
            Assert.AreEqual("Running", missing.Status.State);
            Assert.IsFalse(missing.Status.Ready);
            Assert.IsNull(missing.PeerRoot);
        }
        var approval = WindowsSmbTailnet.ParseStatus("nas.example.ts.net", json.Replace("Running", "NeedsMachineAuth"));
        Assert.AreEqual("NeedsMachineAuth", approval.Status.State);
        Assert.IsFalse(approval.Status.Ready);
    }

    [TestMethod]
    public async Task PrivateWindowsAdapterStartsWithoutNasOrTailnetOnDisposableRunner()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            Assert.Inconclusive("Real Wintun startup runs only on a disposable GitHub Actions runner.");
        Assert.AreEqual(0, UGREENRemoteDrive.Native.ManagedTailnet.SelfTest());
        var tailnet = new UGREENRemoteDrive.Native.ManagedTailnet();
        await tailnet.EnsureStartedAsync();
        using var status = JsonDocument.Parse(await tailnet.StatusAsync());
        Assert.IsTrue(status.RootElement.TryGetProperty("BackendState", out var state));
        Assert.IsTrue(state.GetString() is "NoState" or "NeedsLogin" or "Stopped");
        Assert.IsTrue(status.RootElement.GetProperty("TUN").GetBoolean());
        // No real auth key, existing identity, NAS, route, or SMB file is used.
    }

    [TestMethod]
    public void ReusesSameShareWithoutRemappingOrDisconnectingExistingWindowsSession()
    {
        var api = new FakeApi { Slot = new(true, Root.ToUpperInvariant()) };
        var drive = new WindowsSmbDrive(api);
        drive.Connect("u:", Root);
        Assert.IsTrue(drive.IsMapped);
        Assert.IsFalse(drive.OwnsMapping);
        Assert.AreEqual(0, api.MapCalls);
        Assert.IsFalse(drive.Disconnect());
        Assert.AreEqual(0, api.UnmapCalls);
        Assert.IsFalse(drive.IsMapped);
    }

    [TestMethod]
    public void RefusesOtherShareLocalDriveAndDisconnectedUnknownMapping()
    {
        foreach (var slot in new[] { new WindowsDriveSlot(true, @"\\fixture-other\Techbase"), new(true) })
        {
            var api = new FakeApi { Slot = slot };
            var drive = new WindowsSmbDrive(api);
            Assert.ThrowsExactly<IOException>(() => drive.Connect("U:", Root));
            Assert.AreEqual(0, api.MapCalls);
            Assert.AreEqual(0, api.UnmapCalls);
        }
    }

    [TestMethod]
    public void OpenFileRefusalKeepsMappingAndNeverCancelsOtherDriveAfterReplacement()
    {
        var api = new FakeApi();
        var drive = new WindowsSmbDrive(api);
        drive.Connect("U:", Root);
        Assert.IsTrue(drive.OwnsMapping);
        api.UnmapError = 2401;
        Assert.ThrowsExactly<IOException>(() => drive.Disconnect());
        Assert.IsTrue(drive.IsMapped);
        api.Slot = new(true, @"\\replacement\Techbase");
        Assert.IsFalse(drive.Disconnect());
        Assert.AreEqual(1, api.UnmapCalls);
    }

    [TestMethod]
    public void CancelsOnlyOwnSuccessfulMappingAndPropagatesWindowsErrors()
    {
        var api = new FakeApi();
        var drive = new WindowsSmbDrive(api);
        drive.Connect("U:", Root);
        Assert.IsTrue(drive.Disconnect());
        Assert.AreEqual(1, api.MapCalls);
        Assert.AreEqual(1, api.UnmapCalls);
        api.MapError = 1219;
        var ex = Assert.ThrowsExactly<WindowsSmbMappingException>(() => drive.Connect("U:", Root));
        Assert.AreEqual(1219, ex.Error);
        Assert.IsFalse(drive.IsMapped);
    }

    [TestMethod]
    public void RejectsOtherSharesAndPathsBeforeTouchingWindows()
    {
        var api = new FakeApi();
        var drive = new WindowsSmbDrive(api);
        Assert.ThrowsExactly<ArgumentException>(() => drive.Connect("U:", @"\\fixture-nas\Private"));
        Assert.ThrowsExactly<ArgumentException>(() => drive.Connect("U:", Root + @"\child"));
        Assert.ThrowsExactly<ArgumentException>(() => drive.Connect("C:", Root));
        Assert.AreEqual(0, api.InspectCalls);
    }

    [TestMethod]
    public void ExistingSettingsKeepVirtualModeAndNewPreferenceSurvivesSerialization()
    {
        Assert.IsFalse(JsonSerializer.Deserialize<DriveSettings>("{}")!.UseWindowsSmb);
        var settings = new DriveSettings { UseWindowsSmb = true, SmbPath = Root };
        Assert.IsTrue(JsonSerializer.Deserialize<DriveSettings>(JsonSerializer.Serialize(settings))!.UseWindowsSmb);
    }

    private sealed class FakeApi : IWindowsDriveApi
    {
        public WindowsDriveSlot Slot = new(false);
        public int MapCalls, UnmapCalls, InspectCalls, MapError, UnmapError;
        public WindowsDriveSlot Inspect(string letter) { InspectCalls++; return Slot; }
        public int Map(string letter, string root)
        {
            MapCalls++;
            if (MapError == 0) Slot = new(true, root);
            return MapError;
        }
        public int Unmap(string letter)
        {
            UnmapCalls++;
            if (UnmapError == 0) Slot = new(false);
            return UnmapError;
        }
    }
}
