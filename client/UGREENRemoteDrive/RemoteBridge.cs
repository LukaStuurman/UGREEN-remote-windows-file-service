using System.IO;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace UGREENRemoteDrive;

internal sealed record BridgeReply(int Status, bool Ok, string Body, bool Binary, string? Error, bool LoginRedirect = false);

internal sealed record RemoteAuthenticationResult(bool Authenticated, int? HttpStatus, bool LoginRedirect, string? FailureClass)
{
    public string DiagnosticCode => LoginRedirect
        ? "login-redirect"
        : FailureClass == "InvalidRootStatResponse"
            ? "invalid-root-stat-response"
            : HttpStatus is int status
                ? $"http-status-{status}"
                : FailureClass is string failureClass
                    ? $"exception-{failureClass}"
                    : "probe-failed";

    public static RemoteAuthenticationResult FromReply(BridgeReply reply)
    {
        if (reply.LoginRedirect)
            return new RemoteAuthenticationResult(false, null, true, null);
        if (reply.Status == 200 && reply.Ok && IsRootDirectoryStat(reply))
            return new RemoteAuthenticationResult(true, reply.Status, false, null);
        if (reply.Status == 200)
            return new RemoteAuthenticationResult(false, reply.Status, false, "InvalidRootStatResponse");
        if (reply.Status is > 0)
            return new RemoteAuthenticationResult(false, reply.Status, false, null);
        return new RemoteAuthenticationResult(false, null, false,
            reply.Error is null ? null : "BrowserFetchError");
    }

    public static RemoteAuthenticationResult FromException(Exception exception)
    {
        if (exception.GetBaseException() is UgreenLinkLoginRedirectException)
            return new RemoteAuthenticationResult(false, null, true, null);

        var root = exception.GetBaseException();
        return new RemoteAuthenticationResult(false, null, false, root.GetType().Name);
    }

    public static RemoteAuthenticationResult LoginRedirectDetected() =>
        new(false, null, true, null);

    private static bool IsRootDirectoryStat(BridgeReply reply)
    {
        if (reply.Binary) return false;
        try
        {
            using var document = JsonDocument.Parse(reply.Body);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("isDirectory", out var isDirectory) && isDirectory.ValueKind == JsonValueKind.True &&
                root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == "";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal sealed class RemoteAuthenticationState
{
    private readonly object _gate = new();
    private int _generation;
    private int _authenticatedGeneration = -1;

    public bool IsAuthenticated
    {
        get
        {
            lock (_gate) return _authenticatedGeneration == _generation;
        }
    }

    public int CaptureGeneration()
    {
        lock (_gate) return _generation;
    }

    public bool TrySetResult(int generation, bool authenticated)
    {
        lock (_gate)
        {
            if (_generation != generation) return false;
            _authenticatedGeneration = authenticated ? generation : -1;
            return true;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _authenticatedGeneration = -1;
        }
    }
}

internal static class RemoteProbePolicy
{
    public static bool CanProbe(bool serviceRootActive, bool loginFlowActive,
        bool cleanupRequired, bool cleanupInProgress, bool probeInProgress) =>
        serviceRootActive && !loginFlowActive && !cleanupRequired && !cleanupInProgress && !probeInProgress;
}

internal sealed class RemoteBridge
{
    internal readonly record struct PopupTargetComparison(
        bool Https,
        bool HostMatchesConfigured,
        bool DefaultPortMatches,
        bool RootPath,
        bool QueryAbsent,
        bool FragmentAbsent,
        bool UserInfoAbsent,
        bool UgreenDockerHost,
        bool ExactAuthBootstrapPath,
        bool NonemptyBoundedQuery);

    private readonly WebView2 _view;
    private Uri? _serviceOrigin;
    private sealed record PendingRequest(TaskCompletionSource<BridgeReply> Completion, string Source,
        int NavigationGeneration, ulong NavigationId, int AuthenticationGeneration);
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new();
    private readonly RemoteAuthenticationState _authenticationState = new();
    private bool _initialized;
    private int _navigationGeneration;
    private ulong _activeNavigationId;

    public RemoteBridge(WebView2 view)
    {
        _view = view;
    }

    public bool IsAuthenticated => _authenticationState.IsAuthenticated;

    public void Configure(Uri serviceOrigin)
    {
        _serviceOrigin = new Uri(serviceOrigin.GetLeftPart(UriPartial.Authority) + "/");
        _authenticationState.Invalidate();
    }

    public string BuildServiceRootUrl()
    {
        var serviceOrigin = _serviceOrigin ?? throw new InvalidOperationException("UGREENlink is not configured.");
        return serviceOrigin.AbsoluteUri;
    }

    public string BuildHealthUrl()
    {
        var serviceOrigin = _serviceOrigin ?? throw new InvalidOperationException("UGREENlink is not configured.");
        return new Uri(serviceOrigin, "/api/v1/health").AbsoluteUri;
    }

    public bool IsConfiguredServiceOrigin(Uri uri)
    {
        var serviceOrigin = _serviceOrigin;
        return serviceOrigin is not null && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            uri.GetLeftPart(UriPartial.Authority).Equals(serviceOrigin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    }

    public bool IsConfiguredHealthUrl(Uri uri) => IsConfiguredServiceOrigin(uri) &&
        uri.AbsolutePath.Equals("/api/v1/health", StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    public bool IsConfiguredServiceRoot(Uri uri) => IsConfiguredServiceOrigin(uri) &&
        uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    public bool IsConfiguredAuthBootstrapPath(Uri uri) => IsConfiguredServiceOrigin(uri) &&
        uri.AbsolutePath.Equals("/api/ugreen/auth", StringComparison.Ordinal);

    public void ClearConfiguration()
    {
        _serviceOrigin = null;
        _authenticationState.Invalidate();
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
                  const headers = { 'X-UGREEN-Remote-Drive': '1' };
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
                    window.chrome.webview.postMessage({ id: request.id, status: 0, ok: false, body: '', binary: false, error: 'UGREENlink login redirect', loginRedirect: true });
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
        _view.CoreWebView2.NavigationStarting += OnNavigationStarting;
        _view.CoreWebView2.NavigationCompleted += (_, _) => _authenticationState.Invalidate();
        _initialized = true;
    }

    public BridgeReply Send(string method, string url, string? jsonBody = null, byte[]? binaryBody = null,
        bool binaryResponse = false, bool authenticationProbe = false)
    {
        if (!_initialized)
            throw new DriveDisconnectedException("De UGREENlink-browser is nog niet gereed.");

        var target = new Uri(url);
        if (authenticationProbe &&
            (!IsRootStatAuthenticationProbe(method, target) || jsonBody is not null || binaryBody is not null || binaryResponse))
            throw new DriveDisconnectedException("Alleen de root-stat-aanmeldcontrole mag vóór authenticatie worden uitgevoerd.");

        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<BridgeReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new
        {
            id,
            method,
            url,
            body = binaryBody is not null ? Convert.ToBase64String(binaryBody) : jsonBody,
            bodyKind = binaryBody is not null ? "binary" : "json",
            responseKind = binaryResponse ? "binary" : "json"
        };
        var sendAuthenticationGeneration = _authenticationState.CaptureGeneration();

        try
        {
            var json = JsonSerializer.Serialize(request);
            InvokeOnUiThread(() =>
            {
                var core = _view.CoreWebView2 ?? throw new DriveDisconnectedException("De UGREENlink-browser is nog niet gereed.");
                var serviceOrigin = _serviceOrigin ?? throw new DriveDisconnectedException("Vul eerst het UGREENlink-appadres in.");
                if (!IsAllowedOrigin(target, serviceOrigin))
                    throw new DriveDisconnectedException("De API-doelorigin komt niet overeen met de ingestelde UGREENlink-shortcut.");
                var currentSource = core.Source;
                if (!Uri.TryCreate(currentSource, UriKind.Absolute, out var current))
                    throw new DriveDisconnectedException("De UGREENlink-browser is nog niet gereed.");
                if (!CanSendApiRequestFromDocument(current, serviceOrigin, authenticationProbe, IsAuthenticated))
                    throw new DriveDisconnectedException("API-verzoeken zijn gepauzeerd totdat de ingestelde service-root is bevestigd.");
                if (!IsAllowedOrigin(current, serviceOrigin))
                {
                    if (IsUgreenLinkLoginPage(current)) throw new UgreenLinkLoginRedirectException();
                    throw new DriveDisconnectedException("Open eerst de ingestelde UGREENlink-shortcut en meld aan.");
                }
                if (!IsAllowedOrigin(target, serviceOrigin) || !IsAllowedOrigin(current, serviceOrigin))
                    throw new DriveDisconnectedException("De actieve pagina komt niet overeen met de ingestelde UGREENlink-shortcut.");

                var pending = new PendingRequest(completion, currentSource, _navigationGeneration, _activeNavigationId,
                    _authenticationState.CaptureGeneration());
                if (!_pending.TryAdd(id, pending))
                    throw new DriveDisconnectedException("Het API-verzoek kon niet worden gestart.");
                core.PostWebMessageAsJson(json);
                return true;
            });
            return completion.Task.WaitAsync(TimeSpan.FromSeconds(35)).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            _authenticationState.TrySetResult(sendAuthenticationGeneration, false);
            throw new DriveDisconnectedException("UGREENlink reageert niet. Controleer de verbinding en probeer het opnieuw.");
        }
        catch (Exception ex) when (ex is not DriveDisconnectedException)
        {
            _authenticationState.TrySetResult(sendAuthenticationGeneration, false);
            throw new DriveDisconnectedException("De UGREENlink-verbinding is verbroken.", ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public bool TryAuthenticate()
    {
        return TryAuthenticateDetailed().Authenticated;
    }

    public RemoteAuthenticationResult TryAuthenticateDetailed()
    {
        var authenticationGeneration = _authenticationState.CaptureGeneration();
        try
        {
            var reply = Send("GET", BuildApiUrl("stat", new Dictionary<string, string> { ["path"] = "" }),
                authenticationProbe: true);
            var result = RemoteAuthenticationResult.FromReply(reply);
            if (!_authenticationState.TrySetResult(authenticationGeneration, result.Authenticated))
                return RemoteAuthenticationResult.FromException(
                    new DriveDisconnectedException("The active browser navigation changed during authentication."));
            return result;
        }
        catch (Exception ex)
        {
            _authenticationState.TrySetResult(authenticationGeneration, false);
            return RemoteAuthenticationResult.FromException(ex);
        }
    }

    private T InvokeOnUiThread<T>(Func<T> action) => _view.Dispatcher.CheckAccess()
        ? action()
        : _view.Dispatcher.InvokeAsync(action).Task.GetAwaiter().GetResult();

    public string BuildApiUrl(string route, IReadOnlyDictionary<string, string>? query = null)
    {
        var serviceOrigin = _serviceOrigin ?? throw new InvalidOperationException("UGREENlink is not configured.");
        var builder = new UriBuilder(new Uri(serviceOrigin, "/api/v1/" + route));
        if (query is not null)
            builder.Query = string.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        return builder.Uri.AbsoluteUri;
    }

    private static bool IsAllowedOrigin(Uri uri, Uri serviceOrigin) => UgreenLinkAddress.IsAllowedOrigin(uri) &&
        uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        uri.GetLeftPart(UriPartial.Authority).Equals(serviceOrigin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        _navigationGeneration++;
        _activeNavigationId = e.NavigationId;
        _authenticationState.Invalidate();
        foreach (var pending in _pending.Values)
            pending.Completion.TrySetException(new DriveDisconnectedException("De actieve pagina is gewijzigd tijdens het API-verzoek."));
    }

    internal static bool IsUgreenLinkLoginPage(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && IsAllowedDesktopFragment(uri) && uri.Query.Length <= 2049 &&
        (uri.Host.Equals("ug.link", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".ug.link", StringComparison.OrdinalIgnoreCase)) &&
        (uri.AbsolutePath.Equals("/desktop", StringComparison.OrdinalIgnoreCase) ||
         uri.AbsolutePath.Equals("/desktop/", StringComparison.OrdinalIgnoreCase));

    internal static bool IsExpectedHttpLoginRedirect(Uri uri) => uri.Scheme == Uri.UriSchemeHttp &&
        uri.Host.EndsWith(".ug.link", StringComparison.OrdinalIgnoreCase) &&
        !uri.Host.Equals("ug.link", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && IsAllowedDesktopFragment(uri) && uri.Query.Length <= 2049 &&
        uri.AbsolutePath.Equals("/desktop/", StringComparison.OrdinalIgnoreCase);

    internal static bool IsExpectedHttpsLoginRedirect(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.EndsWith(".ug.link", StringComparison.OrdinalIgnoreCase) &&
        !uri.Host.Equals("ug.link", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && IsAllowedDesktopFragment(uri) && uri.Query.Length <= 2049 &&
        uri.AbsolutePath.Equals("/desktop/", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSameExpectedHttpsLoginDocument(string currentSource, Uri target) =>
        Uri.TryCreate(currentSource, UriKind.Absolute, out var current) &&
        IsExpectedHttpsLoginRedirect(current) && IsExpectedHttpsLoginRedirect(target) &&
        current.GetLeftPart(UriPartial.Authority).Equals(target.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) &&
        current.AbsolutePath.Equals(target.AbsolutePath, StringComparison.OrdinalIgnoreCase);

    internal static bool IsTrustedUgreenDesktopDocument(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.EndsWith(".ug.link", StringComparison.OrdinalIgnoreCase) &&
        !uri.Host.Equals("ug.link", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && IsAllowedDesktopFragment(uri) && uri.Query.Length <= 2049 &&
        uri.AbsolutePath.Equals("/desktop/", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedDesktopFragment(Uri uri) =>
        string.IsNullOrEmpty(uri.Fragment) ||
        (uri.Fragment.StartsWith("#/", StringComparison.Ordinal) && uri.Fragment.Length <= 513);

    internal static bool IsAllowedInAppPopupTarget(string sourceFrame, string topLevelSource,
        Uri target, Uri configuredServiceRoot) =>
        IsTrustedUgreenDesktopFrame(sourceFrame, topLevelSource) &&
        IsConfiguredServiceRootTarget(target, configuredServiceRoot);

    internal static bool IsAllowedAuthBootstrapPopupTarget(string sourceFrame, string topLevelSource,
        Uri target, Uri configuredServiceRoot) =>
        IsTrustedUgreenDesktopFrame(sourceFrame, topLevelSource) &&
        IsConfiguredAuthBootstrapTarget(target, configuredServiceRoot);

    internal static bool IsConfiguredAuthBootstrapTarget(Uri target, Uri configuredServiceRoot) =>
        IsAllowedOrigin(target, configuredServiceRoot) && target.IsDefaultPort &&
        string.IsNullOrEmpty(target.UserInfo) && string.IsNullOrEmpty(target.Fragment) &&
        target.AbsolutePath.Equals("/api/ugreen/auth", StringComparison.Ordinal) &&
        target.Query.Length is > 1 and <= 2049;

    internal static bool IsExpectedAuthBootstrapNavigation(Uri target, Uri approvedTarget, bool isRedirected) =>
        !isRedirected && IsConfiguredAuthBootstrapTarget(target, approvedTarget) &&
        string.Equals(target.AbsoluteUri, approvedTarget.AbsoluteUri, StringComparison.Ordinal);

    internal static bool CanSendApiRequestFromDocument(Uri current, Uri configuredServiceRoot,
        bool authenticationProbe, bool authenticated) =>
        IsConfiguredServiceRootDocument(current, configuredServiceRoot) &&
        (authenticationProbe || authenticated);

    internal static bool IsRootStatAuthenticationProbe(string method, Uri target) =>
        method.Equals("GET", StringComparison.OrdinalIgnoreCase) && target.IsDefaultPort &&
        string.IsNullOrEmpty(target.UserInfo) && string.IsNullOrEmpty(target.Fragment) &&
        target.AbsolutePath.Equals("/api/v1/stat", StringComparison.Ordinal) &&
        target.Query.Equals("?path=", StringComparison.Ordinal);

    private static bool IsConfiguredServiceRootTarget(Uri target, Uri configuredServiceRoot) =>
        IsAllowedOrigin(target, configuredServiceRoot) && target.IsDefaultPort &&
        string.IsNullOrEmpty(target.UserInfo) && string.IsNullOrEmpty(target.Query) &&
        string.IsNullOrEmpty(target.Fragment) && target.AbsolutePath == "/" &&
        UgreenLinkAddress.IsAllowedOrigin(configuredServiceRoot);

    private static bool IsConfiguredServiceRootDocument(Uri uri, Uri configuredServiceRoot) =>
        IsAllowedOrigin(uri, configuredServiceRoot) && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath == "/";

    private static bool IsTrustedUgreenDesktopFrame(string frameSource, string topLevelSource)
    {
        if (!Uri.TryCreate(frameSource, UriKind.Absolute, out var frame) ||
            !Uri.TryCreate(topLevelSource, UriKind.Absolute, out var topLevel) ||
            !IsTrustedUgreenDesktopDocument(frameSource) || !IsTrustedUgreenDesktopDocument(topLevelSource)) return false;
        return frame.GetLeftPart(UriPartial.Authority).Equals(
            topLevel.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    }

    internal static PopupTargetComparison ComparePopupTarget(Uri target, Uri configuredServiceRoot) => new(
        Https: target.Scheme == Uri.UriSchemeHttps,
        HostMatchesConfigured: target.Host.Equals(configuredServiceRoot.Host, StringComparison.OrdinalIgnoreCase),
        DefaultPortMatches: target.IsDefaultPort && configuredServiceRoot.IsDefaultPort && target.Port == configuredServiceRoot.Port,
        RootPath: target.AbsolutePath == "/",
        QueryAbsent: string.IsNullOrEmpty(target.Query),
        FragmentAbsent: string.IsNullOrEmpty(target.Fragment),
        UserInfoAbsent: string.IsNullOrEmpty(target.UserInfo),
        UgreenDockerHost: target.Host.EndsWith(".ugdocker.link", StringComparison.OrdinalIgnoreCase),
        ExactAuthBootstrapPath: target.AbsolutePath.Equals("/api/ugreen/auth", StringComparison.Ordinal),
        NonemptyBoundedQuery: target.Query.Length is > 1 and <= 2049);

    internal static bool ShouldUpgradeHttpLoginRedirect(Uri target, bool isRedirected, ulong navigationId,
        ulong? eligibleNavigationId, bool fromConfiguredRootOrHealth, bool upgradeAlreadyUsed) =>
        isRedirected && eligibleNavigationId == navigationId && fromConfiguredRootOrHealth &&
        !upgradeAlreadyUsed && IsExpectedHttpLoginRedirect(target);

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var core = _view.CoreWebView2;
            var configuredOrigin = _serviceOrigin;
            if (core is null || configuredOrigin is null) return;
            if (!SameConfiguredDocument(e.Source, core.Source, configuredOrigin))
            {
                foreach (var request in _pending.Values)
                    request.Completion.TrySetException(new DriveDisconnectedException("Een browserantwoord van een andere of verouderde pagina is genegeerd."));
                return;
            }
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var id = root.GetProperty("id").GetString();
            if (id is null || !_pending.TryGetValue(id, out var pending)) return;
            if (pending.NavigationGeneration != _navigationGeneration || pending.NavigationId != _activeNavigationId ||
                !string.Equals(pending.Source, e.Source, StringComparison.Ordinal))
            {
                pending.Completion.TrySetException(new DriveDisconnectedException("Een verouderd browserantwoord is genegeerd."));
                return;
            }
            var reply = new BridgeReply(
                root.TryGetProperty("status", out var status) ? status.GetInt32() : 0,
                root.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
                root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                root.TryGetProperty("binary", out var binary) && binary.GetBoolean(),
                root.TryGetProperty("error", out var error) ? error.GetString() : null,
                root.TryGetProperty("loginRedirect", out var loginRedirect) && loginRedirect.GetBoolean());
            if (reply.LoginRedirect || reply.Status is 401 or 403)
                _authenticationState.TrySetResult(pending.AuthenticationGeneration, false);
            pending.Completion.TrySetResult(reply);
        }
        catch (Exception ex)
        {
            DriveLog.Error("Browser bridge message could not be processed: " + ex.GetType().Name);
        }
    }

    internal static bool SameConfiguredDocument(string source, string currentSource, Uri configuredOrigin)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var sourceUri) ||
            !Uri.TryCreate(currentSource, UriKind.Absolute, out var currentUri)) return false;
        return IsConfiguredServiceRootDocument(sourceUri, configuredOrigin) &&
            IsConfiguredServiceRootDocument(currentUri, configuredOrigin) &&
            string.Equals(source, currentSource, StringComparison.Ordinal);
    }
}

internal class DriveDisconnectedException : IOException
{
    public DriveDisconnectedException(string message) : base(message) { }
    public DriveDisconnectedException(string message, Exception inner) : base(message, inner) { }
}

internal sealed class UgreenLinkLoginRedirectException : DriveDisconnectedException
{
    public UgreenLinkLoginRedirectException() : base("UGREENlink redirected to sign-in.") { }
}

internal sealed class DriveApiException(int status, string message) : IOException(message)
{
    public int Status { get; } = status;
}
