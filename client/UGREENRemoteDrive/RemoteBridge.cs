using System.IO;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace UGREENRemoteDrive;

internal sealed record BridgeReply(int Status, bool Ok, string Body, bool Binary, string? Error);

internal sealed class RemoteBridge
{
    private readonly WebView2 _view;
    private Uri? _serviceOrigin;
    private string _token = "";
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeReply>> _pending = new();
    private bool _initialized;

    public RemoteBridge(WebView2 view)
    {
        _view = view;
    }

    public bool IsAuthenticated { get; private set; }

    public void Configure(Uri serviceOrigin, string token)
    {
        _serviceOrigin = new Uri(serviceOrigin.GetLeftPart(UriPartial.Authority) + "/");
        _token = token;
        IsAuthenticated = false;
    }

    public void ClearConfiguration()
    {
        _serviceOrigin = null;
        _token = "";
        IsAuthenticated = false;
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        await _view.EnsureCoreWebView2Async();
        const string script = """
            (() => {
              if (window.top !== window || !window.chrome?.webview) return;
              window.chrome.webview.addEventListener('message', async event => {
                const request = event.data;
                if (!request || !request.id || window.top !== window) return;
                try {
                  const target = new URL(request.url, location.href);
                  if (target.origin !== location.origin) throw new Error('The active page is not the configured UGREENlink app.');
                  const headers = { 'Authorization': 'Bearer ' + request.token };
                  let body = undefined;
                  if (request.body !== null) {
                    if (request.bodyKind === 'binary') {
                      const raw = atob(request.body);
                      const bytes = new Uint8Array(raw.length);
                      for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
                      body = bytes;
                    } else {
                      headers['Content-Type'] = 'application/json';
                      body = request.body;
                    }
                  }
                  const response = await fetch(target.href, {
                    method: request.method,
                    headers,
                    body,
                    credentials: 'include',
                    cache: 'no-store',
                    redirect: 'manual'
                  });
                  if (response.type === 'opaqueredirect') {
                    window.chrome.webview.postMessage({ id: request.id, status: 401, ok: false, body: '{"error":"UGREENlink-aanmelding vereist"}', binary: false });
                    return;
                  }
                  if (request.responseKind === 'binary') {
                    const bytes = new Uint8Array(await response.arrayBuffer());
                    let raw = '';
                    for (let i = 0; i < bytes.length; i += 0x8000) {
                      raw += String.fromCharCode(...bytes.subarray(i, Math.min(i + 0x8000, bytes.length)));
                    }
                    window.chrome.webview.postMessage({ id: request.id, status: response.status, ok: response.ok, body: btoa(raw), binary: true });
                  } else {
                    window.chrome.webview.postMessage({ id: request.id, status: response.status, ok: response.ok, body: await response.text(), binary: false });
                  }
                } catch (error) {
                  window.chrome.webview.postMessage({ id: request.id, status: 0, ok: false, body: '', binary: false, error: String(error?.message || error) });
                }
              });
            })();
            """;
        await _view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
        _view.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        _view.CoreWebView2.NavigationCompleted += (_, _) => { IsAuthenticated = false; };
        _initialized = true;
    }

    public BridgeReply Send(string method, string url, string? jsonBody = null, byte[]? binaryBody = null, bool binaryResponse = false)
    {
        if (!_initialized || _view.CoreWebView2 is null)
            throw new DriveDisconnectedException("De UGREENlink-browser is nog niet gereed.");

        var serviceOrigin = _serviceOrigin ?? throw new DriveDisconnectedException("Vul eerst het UGREENlink-appadres in.");
        var current = new Uri(_view.CoreWebView2.Source);
        var target = new Uri(url);
        if (!IsAllowedOrigin(current, serviceOrigin) || !IsAllowedOrigin(target, serviceOrigin) || current.GetLeftPart(UriPartial.Authority) != target.GetLeftPart(UriPartial.Authority))
            throw new DriveDisconnectedException("Open eerst de UGREENlink-snelkoppeling en meld aan.");

        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<BridgeReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        var request = new
        {
            id,
            method,
            url,
            token = _token,
            body = binaryBody is not null ? Convert.ToBase64String(binaryBody) : jsonBody,
            bodyKind = binaryBody is not null ? "binary" : "json",
            responseKind = binaryResponse ? "binary" : "json"
        };

        try
        {
            var json = JsonSerializer.Serialize(request);
            var post = _view.Dispatcher.InvokeAsync(() => _view.CoreWebView2.PostWebMessageAsJson(json));
            post.Task.GetAwaiter().GetResult();
            return completion.Task.WaitAsync(TimeSpan.FromSeconds(35)).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            IsAuthenticated = false;
            throw new DriveDisconnectedException("UGREENlink reageert niet. Controleer de verbinding en probeer het opnieuw.");
        }
        catch (Exception ex) when (ex is not DriveDisconnectedException)
        {
            IsAuthenticated = false;
            throw new DriveDisconnectedException("De UGREENlink-verbinding is verbroken.", ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public bool TryAuthenticate()
    {
        try
        {
            var reply = Send("GET", BuildApiUrl("stat", new Dictionary<string, string> { ["path"] = "" }));
            IsAuthenticated = reply.Ok;
            return IsAuthenticated;
        }
        catch
        {
            IsAuthenticated = false;
            return false;
        }
    }

    public string BuildApiUrl(string route, IReadOnlyDictionary<string, string>? query = null)
    {
        var serviceOrigin = _serviceOrigin ?? throw new InvalidOperationException("UGREENlink is not configured.");
        var builder = new UriBuilder(new Uri(serviceOrigin, "/api/v1/" + route));
        if (query is not null)
            builder.Query = string.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        return builder.Uri.AbsoluteUri;
    }

    private static bool IsAllowedOrigin(Uri uri, Uri serviceOrigin) => uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.EndsWith(".ugapp.link", StringComparison.OrdinalIgnoreCase) &&
        uri.GetLeftPart(UriPartial.Authority).Equals(serviceOrigin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var id = root.GetProperty("id").GetString();
            if (id is null || !_pending.TryGetValue(id, out var completion)) return;
            var reply = new BridgeReply(
                root.TryGetProperty("status", out var status) ? status.GetInt32() : 0,
                root.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
                root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                root.TryGetProperty("binary", out var binary) && binary.GetBoolean(),
                root.TryGetProperty("error", out var error) ? error.GetString() : null);
            if (reply.Status == 401) IsAuthenticated = false;
            if (reply.Status is >= 200 and < 300) IsAuthenticated = true;
            completion.TrySetResult(reply);
        }
        catch (Exception ex)
        {
            DriveLog.Error("Browser bridge message could not be processed: " + ex.GetType().Name);
        }
    }
}

internal sealed class DriveDisconnectedException : IOException
{
    public DriveDisconnectedException(string message) : base(message) { }
    public DriveDisconnectedException(string message, Exception inner) : base(message, inner) { }
}

internal sealed class DriveApiException(int status, string message) : IOException(message)
{
    public int Status { get; } = status;
}
