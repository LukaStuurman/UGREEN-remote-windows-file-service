using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using UGREENRemoteDrive;

namespace UGREENRemoteDrive.Tests;

[TestClass]
public sealed class NativeTransportTests
{
    [TestMethod]
    public async Task RealWebViewSharedBuffersRoundTripFourMiBAndKeepOriginGuards()
    {
        // Do not create a browser/profile on the user's computer for tests.
        // This is an isolated, invisible HWND on a disposable Windows CI runner.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            Assert.Inconclusive("Native WebView2 integration runs only on disposable GitHub Actions Windows runners.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            async Task Run()
            {
                try { await ExerciseBridge(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
            _ = Run();
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static async Task ExerciseBridge()
    {
        var profile = Path.Combine(Path.GetTempPath(), "ugreen-bridge-test-" + Guid.NewGuid().ToString("N"));
        using var source = new HwndSource(new HwndSourceParameters("Isolated UGREEN bridge test")
        {
            WindowStyle = unchecked((int)0x80000000), Width = 1, Height = 1
        });
        using var view = new WebView2();
        source.RootVisual = view;
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
        await view.EnsureCoreWebView2Async(environment);
        var core = view.CoreWebView2;
        var fixture = Enumerable.Range(0, BinaryTransfer.MaximumLength).Select(i => (byte)(i % 251)).ToArray();
        byte[]? uploaded = null;
        var streams = new List<MemoryStream>();
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) =>
        {
            // All requests are answered in memory. No real NAS, DNS lookup,
            // existing browser profile, file share or internet fixture is used.
            var uri = new Uri(args.Request.Uri);
            byte[] body;
            var contentType = "application/json";
            if (!uri.AbsolutePath.StartsWith("/api/v1/", StringComparison.Ordinal))
            {
                body = Encoding.UTF8.GetBytes("<!doctype html><title>Isolated bridge fixture</title>");
                contentType = "text/html";
            }
            else
            {
                Assert.AreEqual("1", args.Request.Headers.GetHeader("X-UGREEN-Remote-Drive"));
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                if (uri.AbsolutePath == "/api/v1/read")
                {
                    var offset = int.Parse(query["offset"]!);
                    var length = int.Parse(query["length"]!);
                    body = query["path"] == "oversized" ? new byte[length + 1]
                        : fixture.AsSpan(offset, Math.Min(length, fixture.Length - offset)).ToArray();
                    contentType = "application/octet-stream";
                }
                else if (uri.AbsolutePath == "/api/v1/write")
                {
                    using var received = new MemoryStream();
                    args.Request.Content.CopyTo(received);
                    uploaded = received.ToArray();
                    body = Encoding.UTF8.GetBytes("{\"written\":" + uploaded.Length + "}");
                }
                else body = Encoding.UTF8.GetBytes("{\"name\":\"\",\"isDirectory\":true}");
            }
            var stream = new MemoryStream(body);
            streams.Add(stream);
            args.Response = environment.CreateWebResourceResponse(stream, 200, "OK",
                $"Content-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store");
        };
        var bridge = new RemoteBridge(view);
        await bridge.InitializeAsync();
        bridge.Configure(new Uri("https://fixture.ugdocker.link/"));
        var navigated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.NavigationCompleted += (_, args) =>
        {
            if (args.IsSuccess) navigated.TrySetResult();
            else navigated.TrySetException(new IOException("Fixture navigation failed: " + args.WebErrorStatus));
        };
        core.Navigate(bridge.BuildServiceRootUrl());
        await navigated.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            Assert.IsTrue(await Task.Run(bridge.TryAuthenticate));
            var backend = new RemoteBackend(bridge);
            var read = await Task.Run(() => backend.Read("fixture", 0, fixture.Length));
            CollectionAssert.AreEqual(fixture, read);
            Assert.AreEqual(fixture.Length, await Task.Run(() => backend.Write("fixture", 0, fixture)));
            CollectionAssert.AreEqual(fixture, uploaded!);
            var empty = await Task.Run(() => backend.Read("fixture", fixture.Length, 4096));
            Assert.AreEqual(0, empty.Length);
            await Assert.ThrowsExactlyAsync<DriveDisconnectedException>(() =>
                Task.Run(() => backend.Read("oversized", 0, 4096)));
            await Assert.ThrowsExactlyAsync<DriveDisconnectedException>(() => Task.Run(() =>
                bridge.Send("GET", "https://other.ugdocker.link/api/v1/stat?path=")));
        }
        finally
        {
            // CI deletes the disposable runner/profile after this job. Release
            // native views now; no installation or user browser state is touched.
            view.Dispose();
            foreach (var stream in streams) stream.Dispose();
        }
    }
}
