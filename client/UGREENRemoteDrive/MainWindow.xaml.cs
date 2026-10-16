using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using DokanNet;
using DokanNet.Logging;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using WinForms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;

namespace UGREENRemoteDrive;

public partial class MainWindow : Window
{
    private enum BrowserPage { Unknown, IntermediateBlank, ServiceRoot, Health, AuthBootstrap, Login, OtherService, Blocked }
    private sealed record PendingHttpsLoginUpgrade(Uri Target, BrowserPage EntryPage, int Generation, string ConfiguredOrigin);
    private sealed record PendingAutomaticTileOpen(Uri DesktopOrigin, Uri ServiceRoot, int Generation);
    private readonly Dictionary<ulong, BrowserPage> _loginRedirectEligibleNavigations = new();
    private readonly Dictionary<ulong, PendingHttpsLoginUpgrade> _pendingHttpsLoginUpgrades = new();
    private readonly HashSet<ulong> _httpUpgradeAttemptedNavigations = new();
    private readonly HashSet<ulong> _authBootstrapNavigationIds = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly WinForms.NotifyIcon _trayIcon;
    private DriveSettings _settings = new();
    private RemoteBridge? _bridge;
    private RemoteBackend? _remote;
    private BackendSelector? _backends;
    private IFileBackend? _mountedBackend;
    private string _backendSmbPath = "";
    private Dokan? _dokan;
    private DokanInstance? _dokanInstance;
    private DateTimeOffset _lastRemoteProbe;
    private string? _lastRemoteFailureDiagnostic;
    private string? _mountedPoint;
    private bool _exitRequested;
    private bool _autoMountAttempted;
    private bool _connectAndMountRequested;
    private bool _loginFlowActive;
    private bool _healthNavigationAttempted;
    private bool _returningToRoot;
    private bool _probeInProgress;
    private int _navigationGeneration;
    private ulong _activeNavigationId;
    private int? _healthResponseStatus;
    private BrowserPage _activePage;
    private bool _serviceNavigationStarted;
    private bool _shortcutDiscoveryActive;
    private Uri? _pendingAuthBootstrapTarget;
    private bool _ticketBootstrapCleanupRequired;
    private bool _ticketBootstrapCleanupInProgress;
    private PendingAutomaticTileOpen? _pendingAutomaticTileOpen;
    private int _automaticTileOpenAttemptedGeneration = -1;
    private DesktopTileSelection? _lastDesktopSelection;
    private int _lastDesktopSelectionGeneration = -1;
    private DateTimeOffset _lastDesktopSelectionAt;
    private readonly SmbSignInService _smbSignIn = SmbSignInService.Create();
    private readonly HashSet<string> _savedSmbSignInAttempted = new(StringComparer.OrdinalIgnoreCase);
    private bool _nativeSignInActive;
    private bool _connectionActionInProgress;
    private EmbeddedTailscaleBackend? _embeddedTailscale;
    private Window? _ugreenLoginWindow;

    public MainWindow()
    {
        InitializeComponent();
        DriveLetterBox.ItemsSource = Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(code => ((char)code) + ":")
            .Where(letter => !DriveInfo.GetDrives().Any(drive => drive.Name.Equals(letter + "\\", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        DriveLetterBox.SelectedItem = "U:";
        _trayIcon = CreateTrayIcon();
        Loaded += OnLoaded;
        Closing += OnClosing;
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetConfigurationControlsEnabled(false);
        _settings = SettingsStore.Load();
        RemoteUrlBox.Text = string.IsNullOrWhiteSpace(_settings.RemoteUrl) ? _settings.NasPortalUrl : _settings.RemoteUrl;
        SmbPathBox.Text = _settings.SmbPath;
        TailscaleHostBox.Text = _settings.TailscaleHost;
        if (DriveLetterBox.Items.Contains(_settings.DriveLetter)) DriveLetterBox.SelectedItem = _settings.DriveLetter;
        StartupBox.IsChecked = _settings.AutoStart;
        RememberLoginBox.IsChecked = _settings.RememberLogin;
        AutoOpenShortcutBox.IsChecked = _settings.AutoOpenShortcut;
        ShortcutTileNameBox.Text = _settings.ShortcutTileName;
        _connectAndMountRequested = _settings.AutoOpenShortcut &&
            (!string.IsNullOrWhiteSpace(_settings.RemoteUrl) || !string.IsNullOrWhiteSpace(_settings.NasPortalUrl) ||
             !string.IsNullOrWhiteSpace(_settings.SmbPath) || !string.IsNullOrWhiteSpace(_settings.TailscaleHost));
        try
        {
            Directory.CreateDirectory(SettingsStore.AppDirectory);
            var profile = Path.Combine(SettingsStore.AppDirectory, "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, profile);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            // Use the browser's own per-Windows-user password manager. The client
            // never reads password fields, cookies, or the password database.
            Browser.CoreWebView2.Profile.IsPasswordAutosaveEnabled = _settings.RememberLogin;
            _bridge = new RemoteBridge(Browser);
            await _bridge.InitializeAsync();
            _remote = new RemoteBackend(_bridge);
            Browser.NavigationStarting += Browser_NavigationStarting;
            Browser.NavigationCompleted += Browser_NavigationCompleted;
            Browser.CoreWebView2.NewWindowRequested += Browser_NewWindowRequested;
            Browser.CoreWebView2.WebResourceResponseReceived += Browser_WebResourceResponseReceived;
            Browser.CoreWebView2.SourceChanged += Browser_SourceChanged;
            Browser.CoreWebView2.WebMessageReceived += Browser_DesktopSelectionReceived;
            CreateUgreenLoginPopup();
        }
        catch (Exception ex)
        {
            StatusText.Text = "WebView2 kon niet starten. Installeer zo nodig de officiële Microsoft Edge WebView2 Runtime.";
            DriveLog.Error("WebView2 initialization failed: " + ex.GetType().Name);
            SetConfigurationControlsEnabled(true);
            return;
        }

        TryConfigureFromSettings();
        RebuildBackends();
        if (_backends is not null) await _backends.ProbeSmbAsync(useRecentResult: true);
        if (await TryMountFromSmbAsync(restoreSavedSignIn: true))
        {
            // A direct connection does not open the NAS web desktop.
        }
        else if (_backends?.SmbReachable == true)
        {
            StatusText.Text = "De lokale Techbase-share is bereikbaar. Klik op Verbinden om de schijf te openen.";
        }
        else if (_connectAndMountRequested && !string.IsNullOrWhiteSpace(_settings.TailscaleHost))
        {
            await ConnectEmbeddedTailscaleAsync();
        }
        else if (_connectAndMountRequested && !string.IsNullOrWhiteSpace(_settings.SmbPath) &&
                 await SmbSignInService.CanContactServerAsync(_settings.SmbPath))
        {
            await SignInAndMountSmbAsync();
        }
        else if (TryConfigureFromSettings())
        {
            ShowRemoteBrowser(true);
            Browser.Source = new Uri(_bridge!.BuildServiceRootUrl());
            StatusText.Text = "UGREENlink openen. Meld aan in dit appvenster; Edge-aanmelding wordt niet overgenomen.";
        }
        else if (TryStartSavedNasPortalDiscovery())
        {
            // Keep the window visible so a first-time sign-in and shortcut selection can finish.
        }
        _statusTimer.Start();
        SetConfigurationControlsEnabled(_dokanInstance is null);
        if (!_shortcutDiscoveryActive && _ugreenLoginWindow?.IsVisible != true && Environment.GetCommandLineArgs().Contains("--minimized", StringComparer.OrdinalIgnoreCase)) Hide();
    }

    private void Browser_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        // UGOS signs in through a SPA route change; there need not be another
        // NavigationCompleted event after the login form disappears.
        if (e.IsNewDocument || Browser.CoreWebView2 is not { } core ||
            !RemoteBridge.IsTrustedUgreenDesktopDocument(core.Source)) return;
        _automaticTileOpenAttemptedGeneration = -1;
        _pendingAutomaticTileOpen = null;
        TryStartAutomaticTileOpen();
    }

    private void ConnectionPreferencesChanged(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is not { } core) return;
        try
        {
            _settings.RememberLogin = RememberLoginBox.IsChecked == true;
            _settings.AutoOpenShortcut = AutoOpenShortcutBox.IsChecked == true;
            core.Profile.IsPasswordAutosaveEnabled = _settings.RememberLogin;
            SettingsStore.Save(_settings);
            if (!_settings.AutoOpenShortcut) _connectAndMountRequested = false;
            _automaticTileOpenAttemptedGeneration = -1;
            _pendingAutomaticTileOpen = null;
            TryStartAutomaticTileOpen();
        }
        catch (Exception ex)
        {
            DriveLog.Error("Connection preferences could not be saved: " + ex.GetType().Name);
            StatusText.Text = "De aanmeldvoorkeuren konden niet worden opgeslagen. Probeer opnieuw.";
        }
    }

    private void Browser_DesktopSelectionReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var core = Browser.CoreWebView2;
        if (core is null || !RemoteBridge.IsTrustedUgreenDesktopFrame(e.Source, core.Source) ||
            e.WebMessageAsJson.Length > 2048) return;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "ugreen-desktop-selection") return;
            var selection = DesktopTileSelection.Validate(root.GetProperty("id").GetString(), root.GetProperty("name").GetString());
            if (selection is null)
            {
                _lastDesktopSelection = null;
                return;
            }
            _lastDesktopSelection = selection;
            _lastDesktopSelectionGeneration = _navigationGeneration;
            _lastDesktopSelectionAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { }
    }

    private void RememberApprovedDesktopSelection()
    {
        if (_lastDesktopSelection is not { } selection || _lastDesktopSelectionGeneration != _navigationGeneration ||
            DateTimeOffset.UtcNow - _lastDesktopSelectionAt > TimeSpan.FromSeconds(10)) return;
        // This method is called only after the popup destination has passed the
        // configured-origin (or explicit first-time discovery) checks.
        _settings.ShortcutTileId = selection.Id;
        _settings.ShortcutTileName = selection.Name;
        ShortcutTileNameBox.Text = selection.Name;
        SettingsStore.Save(_settings);
        _lastDesktopSelection = null;
    }

    private void Browser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        _navigationGeneration++;
        if (_pendingAutomaticTileOpen is { } automaticPermit && automaticPermit.Generation != _navigationGeneration)
            _pendingAutomaticTileOpen = null;
        _activeNavigationId = e.NavigationId;
        if (e.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            _activePage = BrowserPage.IntermediateBlank;
            DriveLog.Info("Top-level navigation intermediate: target=about-blank; allowed only as an inert WebView transition.");
            if (_serviceNavigationStarted)
            {
                _loginFlowActive = true;
                StatusText.Text = "UGREENlink gebruikt een tijdelijke lege pagina; er is nog geen aanmeldscherm geladen. Klik op Verbinden om terug te gaan naar de NAS-shortcut.";
            }
            return;
        }
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            e.Cancel = true;
            _activePage = BrowserPage.Blocked;
            DriveLog.Error("Top-level navigation blocked: invalid destination URI.");
            StatusText.Text = "Alleen beveiligde HTTPS-pagina's van de NAS-shortcut of UGREENlink-aanmelding zijn toegestaan.";
            return;
        }
        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            var entryPage = BrowserPage.Unknown;
            var hasNavigationContext = _loginRedirectEligibleNavigations.TryGetValue(e.NavigationId, out entryPage);
            var eligibleEntry = e.IsRedirected && hasNavigationContext &&
                entryPage is BrowserPage.ServiceRoot or BrowserPage.Health or BrowserPage.AuthBootstrap;
            var expectedLoginHost = uri.Host.EndsWith(".ug.link", StringComparison.OrdinalIgnoreCase) &&
                !uri.Host.Equals("ug.link", StringComparison.OrdinalIgnoreCase);
            var exactDesktopPath = uri.AbsolutePath.Equals("/desktop/", StringComparison.OrdinalIgnoreCase);
            var defaultPort = uri.IsDefaultPort;
            var userInfoAbsent = string.IsNullOrEmpty(uri.UserInfo);
            var fragmentAbsent = string.IsNullOrEmpty(uri.Fragment);
            var fragmentIsSpaRoot = uri.Fragment.Equals("#/", StringComparison.Ordinal);
            var fragmentIsSpaRoute = uri.Fragment.StartsWith("#/", StringComparison.Ordinal);
            var fragmentBounded = uri.Fragment.Length <= 513;
            var queryBounded = uri.Query.Length <= 2049;
            var upgradeAlreadyUsed = _httpUpgradeAttemptedNavigations.Contains(e.NavigationId);
            DriveLog.Info(
                "HTTP navigation assessment: " +
                $"redirected={e.IsRedirected}, navigation-context={hasNavigationContext}, entry={entryPage}, eligible-entry={eligibleEntry}, " +
                $"expected-login-host={expectedLoginHost}, exact-desktop-path={exactDesktopPath}, default-port={defaultPort}, " +
                $"userinfo-absent={userInfoAbsent}, fragment-absent={fragmentAbsent}, fragment-is-spa-root={fragmentIsSpaRoot}, " +
                $"fragment-is-spa-route={fragmentIsSpaRoute}, fragment-bounded={fragmentBounded}, query-bounded={queryBounded}, " +
                $"upgrade-already-used={upgradeAlreadyUsed}, navigation={e.NavigationId}.");
            var entryNavigationId = eligibleEntry ? e.NavigationId : (ulong?)null;
            var upgradeAllowed = RemoteBridge.ShouldUpgradeHttpLoginRedirect(uri, e.IsRedirected, e.NavigationId,
                entryNavigationId, eligibleEntry, upgradeAlreadyUsed);
            if (upgradeAllowed)
            {
                e.Cancel = true;
                _httpUpgradeAttemptedNavigations.Add(e.NavigationId);
                _loginRedirectEligibleNavigations.Remove(e.NavigationId);
                _activePage = BrowserPage.IntermediateBlank;
                _loginFlowActive = true;
                StatusText.Text = "UGREENlink stuurde de aanmelding via HTTP; alleen de HTTPS-versie wordt geopend.";
                DriveLog.Info($"HTTP login redirect upgraded: entry={entryPage}, expected-domain=ug.link, navigation={e.NavigationId}.");
                var secureTarget = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
                if (_bridge is null || !RemoteBridge.IsExpectedHttpsLoginRedirect(secureTarget))
                {
                    _httpUpgradeAttemptedNavigations.Remove(e.NavigationId);
                    _activePage = BrowserPage.Blocked;
                    DriveLog.Error("HTTP login redirect canceled, but its HTTPS target failed strict validation.");
                    StatusText.Text = "De veilige UGREENlink-aanmelding kon niet worden gevalideerd. Probeer Verbinden opnieuw.";
                    return;
                }

                var configuredOrigin = new Uri(_bridge.BuildServiceRootUrl()).GetLeftPart(UriPartial.Authority);
                var pending = new PendingHttpsLoginUpgrade(secureTarget, entryPage, _navigationGeneration, configuredOrigin);
                _pendingHttpsLoginUpgrades[e.NavigationId] = pending;
                DriveLog.Info("Insecure login redirect canceled; waiting for its matching canceled-navigation completion before opening HTTPS.");
                _ = WatchForCanceledLoginRedirectCompletionAsync(e.NavigationId, pending);
                return;
            }

            e.Cancel = true;
            _activePage = BrowserPage.Blocked;
            _loginFlowActive = true;
            DriveLog.Error("Top-level navigation blocked: HTTP target did not match the one-shot UGREENlink redirect rule.");
            StatusText.Text = "Een onbeveiligde HTTP-bestemming is geblokkeerd. Alleen een herkende UGREENlink-aanmeldredirect vanaf de NAS-shortcut kan naar HTTPS worden opgewaardeerd.";
            return;
        }
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            e.Cancel = true;
            _activePage = BrowserPage.Blocked;
            DriveLog.Error("Top-level navigation blocked: non-HTTPS target.");
            StatusText.Text = "Alleen beveiligde HTTPS-pagina's van de NAS-shortcut of UGREENlink-aanmelding zijn toegestaan.";
            return;
        }
        if (_shortcutDiscoveryActive && RemoteBridge.IsUgreenLinkPortalNavigation(uri))
        {
            _activePage = BrowserPage.Login;
            _loginFlowActive = true;
            DriveLog.Info("UGREENlink NAS portal navigation allowed during first-time shortcut discovery.");
            StatusText.Text = "Meld aan bij de NAS in dit venster en open daarna de Remote Drive-tegel. Het app-adres wordt automatisch opgeslagen.";
            return;
        }
        if (_bridge?.IsConfiguredServiceOrigin(uri) == true)
        {
            var navigatingFromLogin = _activePage == BrowserPage.Login;
            var fromAuthBootstrapRedirect = e.IsRedirected && _authBootstrapNavigationIds.Contains(e.NavigationId);
            var navigatingFromAuthBootstrap = _activePage == BrowserPage.AuthBootstrap || fromAuthBootstrapRedirect;
            _serviceNavigationStarted = true;
            if (_bridge.IsConfiguredHealthUrl(uri))
            {
                if (navigatingFromAuthBootstrap)
                {
                    e.Cancel = true;
                    _activePage = BrowserPage.Blocked;
                    _loginFlowActive = true;
                    DriveLog.Error("Auth-bootstrap redirect blocked: only the configured service root or approved UGREENlink login may follow.");
                    StatusText.Text = "De shortcut-aanmelding leidde niet naar de service-root. Er zijn geen bestandsverzoeken verstuurd.";
                    return;
                }
                _activePage = BrowserPage.Health;
                if (!e.IsRedirected) _loginRedirectEligibleNavigations[e.NavigationId] = BrowserPage.Health;
                _loginFlowActive = true;
                _healthResponseStatus = null;
                DriveLog.Info("Top-level navigation started: page=health.");
            }
            else if (_bridge.IsConfiguredServiceRoot(uri))
            {
                _activePage = BrowserPage.ServiceRoot;
                if (navigatingFromLogin || navigatingFromAuthBootstrap)
                {
                    _loginFlowActive = false;
                    _healthNavigationAttempted = false;
                    _returningToRoot = false;
                }
                if (!e.IsRedirected) _loginRedirectEligibleNavigations[e.NavigationId] = BrowserPage.ServiceRoot;
                if (fromAuthBootstrapRedirect)
                {
                    _authBootstrapNavigationIds.Remove(e.NavigationId);
                    _loginRedirectEligibleNavigations.Remove(e.NavigationId);
                }
                DriveLog.Info("Top-level navigation started: page=service-root.");
            }
            else if (_bridge.IsConfiguredAuthBootstrapPath(uri))
            {
                var approvedTarget = _pendingAuthBootstrapTarget;
                var matchesOneShotPermit = approvedTarget is not null &&
                    RemoteBridge.IsExpectedAuthBootstrapNavigation(uri, approvedTarget, e.IsRedirected);
                _pendingAuthBootstrapTarget = null;
                if (!matchesOneShotPermit)
                {
                    e.Cancel = true;
                    _activePage = BrowserPage.Blocked;
                    _loginFlowActive = true;
                    DriveLog.Error("Auth-bootstrap navigation blocked: no matching one-shot desktop-popup permit.");
                    StatusText.Text = "De shortcut-aanmelding is niet gestart via de goedgekeurde eenmalige tegelactie.";
                    return;
                }

                _activePage = BrowserPage.AuthBootstrap;
                _loginFlowActive = true;
                _ticketBootstrapCleanupRequired = true;
                _authBootstrapNavigationIds.Add(e.NavigationId);
                _loginRedirectEligibleNavigations[e.NavigationId] = BrowserPage.AuthBootstrap;
                DriveLog.Info("One-shot configured auth-bootstrap navigation started; query omitted.");
                StatusText.Text = "UGREENlink-snelkoppeling controleren. Bestandsverzoeken blijven gepauzeerd tot rootverificatie.";
            }
            else
            {
                e.Cancel = true;
                _activePage = BrowserPage.Blocked;
                _loginFlowActive = true;
                DriveLog.Error("Top-level configured-service navigation blocked: path was not root, health, or the one-shot auth bootstrap.");
                StatusText.Text = "Alleen de service-root, health-controle en goedgekeurde eenmalige UGREENlink-aanmelding zijn toegestaan.";
            }
            return;
        }
        if (RemoteBridge.IsUgreenLinkLoginPage(uri))
        {
            _loginRedirectEligibleNavigations.Remove(e.NavigationId);
            _authBootstrapNavigationIds.Remove(e.NavigationId);
            _activePage = BrowserPage.Login;
            _loginFlowActive = true;
            DriveLog.Info("Top-level navigation started: page=recognized-ugreen-login.");
            StatusText.Text = _shortcutDiscoveryActive
                ? "Meld aan bij de NAS in dit venster en open daarna de Remote Drive-tegel. Het app-adres wordt automatisch opgeslagen."
                : "Meld aan in dit appvenster. De app probeert daarna de opgeslagen Remote Drive-tegel vanzelf te openen.";
            return;
        }
        e.Cancel = true;
        _activePage = BrowserPage.Blocked;
        _loginFlowActive = true;
        DriveLog.Error("Top-level navigation blocked: destination was not an approved service or login page.");
        StatusText.Text = "De bestemming is niet de ingestelde NAS-shortcut of een herkende UGREENlink-aanmeldpagina.";
    }

    private void Browser_WebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        if (_activePage != BrowserPage.Health || _bridge is null ||
            !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || !_bridge.IsConfiguredHealthUrl(uri)) return;
        _healthResponseStatus = e.Response.StatusCode;
        DriveLog.Info($"Health endpoint response received: HTTP {_healthResponseStatus.Value}.");
    }

    private void Browser_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var topLevelSource = Browser.CoreWebView2?.Source ?? "";
        var sourceFrame = e.OriginalSourceFrameInfo?.Source ?? "";
        var sourceCategory = ClassifyNavigationForDiagnostics(topLevelSource);
        var sourceFrameCategory = ClassifyNavigationForDiagnostics(sourceFrame);
        var targetCategory = Uri.TryCreate(e.Uri, UriKind.Absolute, out var requestedTarget)
            ? ClassifyNavigationForDiagnostics(requestedTarget.AbsoluteUri)
            : "invalid";
        var userInitiated = e.IsUserInitiated;
        var automaticTileOpen = false;

        if (!userInitiated && requestedTarget is not null && _pendingAutomaticTileOpen is { } automaticPermit &&
            automaticPermit.Generation == _navigationGeneration &&
            RemoteBridge.IsAllowedAutomaticTilePopupTarget(sourceFrame, topLevelSource, requestedTarget,
                automaticPermit.DesktopOrigin, automaticPermit.ServiceRoot))
        {
            // A single exact shortcut popup may be routed after our narrowly scoped desktop-tile click.
            // The permit is consumed before any asynchronous navigation is queued.
            _pendingAutomaticTileOpen = null;
            automaticTileOpen = true;
            DriveLog.Info("One-shot automatic UGREENlink tile popup approved for the configured shortcut origin.");
        }

        // Never let WebView2 create an uncontrolled popup. Approved, user-initiated shortcut
        // opens are routed back into this same WebView/profile after the event callback returns.
        e.Handled = true;
        DriveLog.Info($"WebView2 new-window request: user-initiated={userInitiated}, frame={sourceFrameCategory}, top-level={sourceCategory}, target={targetCategory}.");

        if (requestedTarget is not null && TryBuildConfiguredServiceRoot(out var diagnosticRoot))
        {
            var comparison = RemoteBridge.ComparePopupTarget(requestedTarget, diagnosticRoot);
            DriveLog.Info(
                "WebView2 popup target comparison (booleans only): " +
                $"https={comparison.Https}, host-matches-configured={comparison.HostMatchesConfigured}, " +
                $"default-port-matches={comparison.DefaultPortMatches}, root-path={comparison.RootPath}, " +
                $"query-absent={comparison.QueryAbsent}, fragment-absent={comparison.FragmentAbsent}, " +
                $"userinfo-absent={comparison.UserInfoAbsent}, ugreen-docker-host={comparison.UgreenDockerHost}, " +
                $"exact-auth-bootstrap-path={comparison.ExactAuthBootstrapPath}, " +
                $"query-nonempty-bounded={comparison.NonemptyBoundedQuery}.");
        }

        if (_shortcutDiscoveryActive && userInitiated && requestedTarget is not null && _bridge is not null &&
            !_ticketBootstrapCleanupRequired && !_ticketBootstrapCleanupInProgress)
        {
            var discoverRoot = RemoteBridge.IsAllowedDiscoveredShortcutRootPopupTarget(
                sourceFrame, topLevelSource, requestedTarget);
            var discoverAuthBootstrap = RemoteBridge.IsAllowedDiscoveredAuthBootstrapPopupTarget(
                sourceFrame, topLevelSource, requestedTarget);
            if (discoverRoot || discoverAuthBootstrap)
            {
                QueueDiscoveredShortcutPopup(sourceFrame, requestedTarget, discoverRoot, discoverAuthBootstrap);
                return;
            }
        }

        if ((!userInitiated && !automaticTileOpen) || requestedTarget is null || _bridge is null ||
            !TryBuildConfiguredServiceRoot(out var configuredRoot) ||
            _ticketBootstrapCleanupRequired || _ticketBootstrapCleanupInProgress)
        {
            DriveLog.Info("WebView2 new-window request blocked by the in-app shortcut policy.");
            if (userInitiated)
                StatusText.Text = "De nieuwe link is niet de ingestelde NAS-shortcut. Open de Remote Drive-snelkoppeling vanuit het UGREENlink-bureaublad.";
            return;
        }

        var openConfiguredRoot = RemoteBridge.IsAllowedInAppPopupTarget(
            sourceFrame, topLevelSource, requestedTarget, configuredRoot);
        var openAuthBootstrap = RemoteBridge.IsAllowedAuthBootstrapPopupTarget(
            sourceFrame, topLevelSource, requestedTarget, configuredRoot);
        if (!openConfiguredRoot && !openAuthBootstrap)
        {
            DriveLog.Info("WebView2 new-window request blocked by the in-app shortcut policy.");
            if (userInitiated)
                StatusText.Text = "De nieuwe link voldoet niet aan de veilige NAS-shortcut-aanmelding.";
            return;
        }

        var scheduledGeneration = _navigationGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_navigationGeneration != scheduledGeneration)
            {
                DriveLog.Info("Approved shortcut popup discarded because a newer navigation superseded it.");
                return;
            }

            var core = Browser.CoreWebView2;
            if (core is null || _bridge is null || !TryBuildConfiguredServiceRoot(out var currentRoot) ||
                !(openConfiguredRoot
                    ? RemoteBridge.IsAllowedInAppPopupTarget(sourceFrame, core.Source, requestedTarget, currentRoot)
                    : RemoteBridge.IsAllowedAuthBootstrapPopupTarget(sourceFrame, core.Source, requestedTarget, currentRoot)))
            {
                DriveLog.Info("Approved shortcut popup discarded after source or configured-origin revalidation failed.");
                StatusText.Text = "De shortcut is gewijzigd of de aanmeldpagina is veranderd. Open de ingestelde Remote Drive-snelkoppeling opnieuw.";
                return;
            }

            _loginFlowActive = true;
            if (openAuthBootstrap)
            {
                _pendingAuthBootstrapTarget = requestedTarget;
                StatusText.Text = "Veilige UGREENlink-shortcut-aanmelding controleren; NAS-bestandsverzoeken wachten op rootverificatie.";
                DriveLog.Info("Exact UGREENlink auth bootstrap popup approved; sensitive query omitted.");
            }
            else
            {
                StatusText.Text = "Remote Drive openen in dit venster. Edge-aanmelding wordt niet met de app gedeeld.";
                DriveLog.Info("Configured UGREENlink shortcut-root popup routed to the primary WebView.");
            }

            try
            {
                RememberApprovedDesktopSelection();
                core.Navigate(requestedTarget.AbsoluteUri);
                if (openAuthBootstrap) _ = ExpireAuthBootstrapPermitAsync(requestedTarget, scheduledGeneration);
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(_pendingAuthBootstrapTarget, requestedTarget)) _pendingAuthBootstrapTarget = null;
                DriveLog.Error("Approved shortcut navigation failed: " + ex.GetType().Name);
                StatusText.Text = "De Remote Drive-snelkoppeling kon niet in dit venster worden geopend.";
            }
        }));
    }

    private void QueueDiscoveredShortcutPopup(string sourceFrame, Uri target, bool openRoot, bool openAuthBootstrap)
    {
        var scheduledGeneration = _navigationGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var core = Browser.CoreWebView2;
            if (core is null || _bridge is null || !_shortcutDiscoveryActive ||
                _navigationGeneration != scheduledGeneration ||
                !(openRoot
                    ? RemoteBridge.IsAllowedDiscoveredShortcutRootPopupTarget(sourceFrame, core.Source, target)
                    : RemoteBridge.IsAllowedDiscoveredAuthBootstrapPopupTarget(sourceFrame, core.Source, target)))
            {
                DriveLog.Info("Discovered UGREENlink shortcut popup discarded after source revalidation failed.");
                StatusText.Text = "De tegel is gewijzigd of niet de Remote Drive-snelkoppeling. Probeer de tegel opnieuw.";
                return;
            }

            try
            {
                // Persist only the stable shortcut origin. Never save the one-time ticket/query.
                var root = new Uri(target.GetLeftPart(UriPartial.Authority) + "/");
                _settings.RemoteUrl = root.AbsoluteUri;
                RememberApprovedDesktopSelection();
                SettingsStore.Save(_settings);
                RemoteUrlBox.Text = root.AbsoluteUri;
                ConfigureRemote();
                RebuildBackends();
                _shortcutDiscoveryActive = false;
                _connectAndMountRequested = true;
                _loginFlowActive = true;

                if (openAuthBootstrap)
                {
                    _pendingAuthBootstrapTarget = target;
                    StatusText.Text = "Remote Drive gevonden. De tijdelijke UGREENlink-aanmelding wordt veilig afgerond.";
                    DriveLog.Info("UGREENlink shortcut host discovered from an approved desktop tile; one-time query omitted from settings and logs.");
                }
                else
                {
                    StatusText.Text = "Remote Drive-adres gevonden en onthouden. Verbinding controleren…";
                    DriveLog.Info("UGREENlink shortcut host discovered from an approved desktop tile; only its origin was saved.");
                }

                core.Navigate(target.AbsoluteUri);
                if (openAuthBootstrap) _ = ExpireAuthBootstrapPermitAsync(target, scheduledGeneration);
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(_pendingAuthBootstrapTarget, target)) _pendingAuthBootstrapTarget = null;
                DriveLog.Error("Discovered UGREENlink shortcut could not be saved/opened: " + ex.GetType().Name);
                StatusText.Text = "Het Remote Drive-adres kon niet worden onthouden. Controleer de app-instellingen en probeer opnieuw.";
            }
        }));
    }

    private async Task ExpireAuthBootstrapPermitAsync(Uri approvedTarget, int scheduledGeneration)
    {
        await Task.Delay(TimeSpan.FromSeconds(6));
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_pendingAuthBootstrapTarget, approvedTarget)) return;
                _pendingAuthBootstrapTarget = null;
                if (_navigationGeneration == scheduledGeneration)
                {
                    DriveLog.Info("One-shot auth-bootstrap permit expired before matching navigation started.");
                    StatusText.Text = "De veilige shortcut-aanmelding is niet gestart. Klik opnieuw op de Remote Drive-tegel.";
                }
            });
        }
        catch (TaskCanceledException) { }
    }

    private bool TryBuildConfiguredServiceRoot(out Uri root)
    {
        root = new Uri("about:blank");
        if (_bridge is null) return false;
        try
        {
            root = new Uri(_bridge.BuildServiceRootUrl());
            return true;
        }
        catch { return false; }
    }

    private string ClassifyNavigationForDiagnostics(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return "invalid";
        if (_bridge is not null && _bridge.IsConfiguredServiceRoot(uri)) return "configured-shortcut-root";
        if (_bridge is not null && _bridge.IsConfiguredHealthUrl(uri)) return "configured-health";
        if (_bridge is not null && _bridge.IsConfiguredAuthBootstrapPath(uri)) return "configured-auth-bootstrap";
        if (RemoteBridge.IsTrustedUgreenDesktopDocument(source)) return "ugreenlink-desktop";
        if (RemoteBridge.IsUgreenLinkLoginPage(uri)) return "ugreenlink-login-page";
        return "other";
    }

    private void Browser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_pendingHttpsLoginUpgrades.Remove(e.NavigationId, out var pendingUpgrade))
        {
            _httpUpgradeAttemptedNavigations.Remove(e.NavigationId);
            if (pendingUpgrade.Generation != _navigationGeneration || e.NavigationId != _activeNavigationId ||
                _activePage != BrowserPage.IntermediateBlank)
            {
                DriveLog.Info("Canceled login-redirect completion discarded because a newer navigation superseded it.");
                return;
            }

            DriveLog.Info($"Canceled HTTP login redirect completed: success={e.IsSuccess}, webError={e.WebErrorStatus}, navigation={e.NavigationId}.");
            if (e.IsSuccess || e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                DriveLog.Error("HTTPS login navigation not started because the HTTP redirect did not complete as an operation-canceled navigation.");
                StatusText.Text = "De onbeveiligde aanmeldredirect is gestopt, maar de veilige aanmeldpagina is niet gestart. Klik op Verbinden om opnieuw te proberen.";
                return;
            }

            QueueHttpsLoginNavigation(e.NavigationId, pendingUpgrade);
            return;
        }
        if (e.NavigationId != _activeNavigationId) return;
        _loginRedirectEligibleNavigations.Remove(e.NavigationId);
        if (_activePage == BrowserPage.IntermediateBlank)
        {
            DriveLog.Info($"Top-level navigation completed: page=intermediate-blank, success={e.IsSuccess}, webError={e.WebErrorStatus}.");
            return;
        }
        if (_ticketBootstrapCleanupRequired)
        {
            _ = CompleteAuthBootstrapNavigationAsync(_navigationGeneration, _activePage, e.IsSuccess);
            return;
        }
        if (_activePage == BrowserPage.AuthBootstrap)
        {
            DriveLog.Error("Auth-bootstrap navigation completed without its cleanup state; remote requests remain blocked.");
            _loginFlowActive = true;
            StatusText.Text = "De shortcut-aanmelding kan niet veilig worden afgerond. Bestandsverzoeken blijven gepauzeerd.";
            return;
        }
        if (_activePage == BrowserPage.Login)
        {
            if (!_nativeSignInActive && _mountedBackend is not (SmbBackend or EmbeddedTailscaleBackend)) ShowRemoteBrowser(true);
            DriveLog.Info($"Top-level navigation completed: page=recognized-ugreen-login, success={e.IsSuccess}, webError={e.WebErrorStatus}.");
            if (!TryStartAutomaticTileOpen())
            {
                StatusText.Text = _shortcutDiscoveryActive
                    ? "Meld aan bij de NAS in dit venster en open de Remote Drive-tegel. De app vult het shortcut-adres daarna zelf in en onthoudt het."
                    : "Meld aan in dit appvenster en klik zo nodig op de Remote Drive-tegel op het UGREENlink-bureaublad.";
            }
            return;
        }
        if (_activePage == BrowserPage.Health)
        {
            DriveLog.Info($"Top-level navigation completed: page=health, success={e.IsSuccess}, webError={e.WebErrorStatus}, http={_healthResponseStatus?.ToString() ?? "unavailable"}.");
            if (e.IsSuccess && _healthResponseStatus == 200 && !_returningToRoot && _bridge is not null)
            {
                _returningToRoot = true;
                Browser.Source = new Uri(_bridge.BuildServiceRootUrl());
            }
            else if (_bridge is not null)
            {
                _loginFlowActive = true;
                StatusText.Text = _healthResponseStatus is int status
                    ? $"De health-aanmeldroute antwoordde met HTTP {status}; er is geen aanmeldpagina bevestigd. Terug naar de NAS-shortcut; controleer de melding en probeer later opnieuw."
                    : $"De health-aanmeldroute kon niet worden bevestigd (WebView2: {e.WebErrorStatus}). Terug naar de NAS-shortcut; er is nog geen aanmeldpagina."
                    ;
                Browser.Source = new Uri(_bridge.BuildServiceRootUrl());
            }
            return;
        }
        if (_activePage != BrowserPage.ServiceRoot || (_loginFlowActive && !_returningToRoot)) return;
        _loginFlowActive = false;
        _returningToRoot = false;
        _ = ProbeRemoteAfterNavigationAsync(_navigationGeneration);
    }

    private async Task CompleteAuthBootstrapNavigationAsync(int generation, BrowserPage completedPage, bool navigationSucceeded)
    {
        if (_ticketBootstrapCleanupInProgress) return;
        _ticketBootstrapCleanupInProgress = true;
        try
        {
            var core = Browser.CoreWebView2;
            if (core is null) throw new InvalidOperationException("WebView2 profile unavailable.");
            await core.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.BrowsingHistory | CoreWebView2BrowsingDataKinds.DiskCache);
            _ticketBootstrapCleanupRequired = false;
            _authBootstrapNavigationIds.Clear();
            DriveLog.Info("Auth-bootstrap browsing history and disk cache cleared; sign-in cookies preserved.");

            if (generation != _navigationGeneration) return;
            if (completedPage == BrowserPage.AuthBootstrap && _activePage == BrowserPage.AuthBootstrap)
            {
                _loginFlowActive = false;
                _returningToRoot = false;
                StatusText.Text = "Shortcut-controle afgerond; de ingestelde service-root wordt nu gecontroleerd.";
                if (_bridge is not null) Browser.Source = new Uri(_bridge.BuildServiceRootUrl());
                return;
            }
            if (completedPage == BrowserPage.ServiceRoot && _activePage == BrowserPage.ServiceRoot && !_loginFlowActive)
            {
                _ = ProbeRemoteAfterNavigationAsync(generation);
                return;
            }
            if (completedPage == BrowserPage.Login && _activePage == BrowserPage.Login)
            {
                if (!_nativeSignInActive && _mountedBackend is not (SmbBackend or EmbeddedTailscaleBackend)) ShowRemoteBrowser(true);
                if (!TryStartAutomaticTileOpen())
                    StatusText.Text = navigationSucceeded
                        ? "Meld aan in dit appvenster en klik zo nodig op de Remote Drive-tegel op het UGREENlink-bureaublad."
                        : "De UGREENlink-aanmeldpagina kon niet worden bevestigd. Klik zo nodig zelf op de Remote Drive-tegel.";
                return;
            }
            if (completedPage == BrowserPage.Blocked)
                StatusText.Text = "De shortcut-aanmelding is geblokkeerd; er zijn geen bestandsverzoeken verstuurd.";
        }
        catch (Exception ex)
        {
            DriveLog.Error("Auth-bootstrap browsing-data cleanup failed: " + ex.GetType().Name);
            if (generation == _navigationGeneration)
            {
                _loginFlowActive = true;
                StatusText.Text = "De tijdelijke shortcut-aanmeldgegevens konden niet worden opgeschoond. Bestandsverzoeken blijven gepauzeerd.";
            }
        }
        finally
        {
            _ticketBootstrapCleanupInProgress = false;
        }
    }

    private async Task WatchForCanceledLoginRedirectCompletionAsync(ulong navigationId, PendingHttpsLoginUpgrade pending)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!_pendingHttpsLoginUpgrades.TryGetValue(navigationId, out var current) || !ReferenceEquals(current, pending)) return;
                _pendingHttpsLoginUpgrades.Remove(navigationId);
                _httpUpgradeAttemptedNavigations.Remove(navigationId);
                if (pending.Generation != _navigationGeneration || navigationId != _activeNavigationId) return;
                DriveLog.Error("Canceled HTTP login redirect completion was not observed; HTTPS login navigation was not attempted.");
                StatusText.Text = "De onbeveiligde aanmeldredirect is gestopt, maar WebView2 bevestigde dit niet. Klik op Verbinden om opnieuw te proberen.";
            });
        }
        catch (TaskCanceledException) { }
    }

    private void QueueHttpsLoginNavigation(ulong canceledNavigationId, PendingHttpsLoginUpgrade pending)
    {
        // Run only after NavigationCompleted has returned to WebView2; navigating from either
        // NavigationStarting or its completion callback can be re-entrant and silently ignored.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_navigationGeneration != pending.Generation || _activeNavigationId != canceledNavigationId ||
                _activePage != BrowserPage.IntermediateBlank)
            {
                DriveLog.Info("Deferred HTTPS login navigation discarded because a newer navigation superseded it.");
                return;
            }

            if (_bridge is null || pending.EntryPage is not (BrowserPage.ServiceRoot or BrowserPage.Health or BrowserPage.AuthBootstrap) ||
                !RemoteBridge.IsExpectedHttpsLoginRedirect(pending.Target))
            {
                DriveLog.Error("Deferred HTTPS login navigation failed strict target or entry-page validation.");
                StatusText.Text = "De veilige UGREENlink-aanmelding kon niet worden gevalideerd. Klik op Verbinden om opnieuw te proberen.";
                return;
            }

            string currentConfiguredOrigin;
            try { currentConfiguredOrigin = new Uri(_bridge.BuildServiceRootUrl()).GetLeftPart(UriPartial.Authority); }
            catch (Exception ex)
            {
                DriveLog.Error("Deferred HTTPS login navigation could not revalidate the configured service: " + ex.GetType().Name);
                StatusText.Text = "De ingestelde NAS-shortcut kon niet opnieuw worden gevalideerd. Klik op Verbinden om opnieuw te proberen.";
                return;
            }
            if (!currentConfiguredOrigin.Equals(pending.ConfiguredOrigin, StringComparison.OrdinalIgnoreCase))
            {
                DriveLog.Info("Deferred HTTPS login navigation discarded because the configured service origin changed.");
                StatusText.Text = "De NAS-shortcut is gewijzigd. Klik op Verbinden om de nieuwe shortcut te openen.";
                return;
            }

            try
            {
                var core = Browser.CoreWebView2;
                if (core is null)
                {
                    DriveLog.Error("Deferred HTTPS login navigation not submitted: WebView2 core unavailable.");
                    StatusText.Text = "WebView2 heeft de veilige aanmeldpagina niet gestart. Klik op Verbinden om opnieuw te proberen.";
                    return;
                }
                if (RemoteBridge.IsSameExpectedHttpsLoginDocument(core.Source, pending.Target))
                {
                    SetExistingUgreenLoginPageStatus();
                    DriveLog.Info("Validated HTTPS login document is already current; redundant navigation skipped.");
                    return;
                }
                DriveLog.Info("Deferred HTTPS login navigation submitted after matching canceled completion.");
                core.Navigate(pending.Target.AbsoluteUri);
                _ = WatchForHttpsLoginNavigationStartAsync(canceledNavigationId, pending);
            }
            catch (Exception ex)
            {
                DriveLog.Error("Deferred HTTPS login navigation submission failed: " + ex.GetType().Name);
                StatusText.Text = "De veilige UGREENlink-aanmelding kon niet worden geopend. Klik op Verbinden om opnieuw te proberen.";
            }
        }));
    }

    private async Task WatchForHttpsLoginNavigationStartAsync(ulong canceledNavigationId, PendingHttpsLoginUpgrade pending)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_navigationGeneration != pending.Generation || _activeNavigationId != canceledNavigationId ||
                    _activePage != BrowserPage.IntermediateBlank) return;
                var currentSource = Browser.CoreWebView2?.Source;
                if (currentSource is not null && RemoteBridge.IsSameExpectedHttpsLoginDocument(currentSource, pending.Target))
                {
                    SetExistingUgreenLoginPageStatus();
                    DriveLog.Info("HTTPS login target is the current approved desktop document; no new navigation-start event was needed.");
                    return;
                }
                DriveLog.Error("No top-level navigation-start event followed the deferred HTTPS login navigation.");
                StatusText.Text = "WebView2 heeft de veilige aanmeldpagina niet geladen. Klik op Verbinden om opnieuw te proberen.";
            });
        }
        catch (TaskCanceledException) { }
    }

    private void SetExistingUgreenLoginPageStatus()
    {
        _activePage = BrowserPage.Login;
        _loginFlowActive = true;
        if (_ticketBootstrapCleanupRequired)
            _ = CompleteAuthBootstrapNavigationAsync(_navigationGeneration, BrowserPage.Login, navigationSucceeded: true);
        else if (!TryStartAutomaticTileOpen())
            StatusText.Text = _shortcutDiscoveryActive
                ? "De UGREENlink-desktop is al open. Meld je zo nodig hier aan en open de Remote Drive-tegel; het shortcut-adres wordt automatisch onthouden."
                : "De UGREENlink-desktop is al open. Meld je zo nodig aan en klik op de Remote Drive-tegel als die niet vanzelf opent.";
    }

    private bool TryStartAutomaticTileOpen()
    {
        var core = Browser.CoreWebView2;
        if (_bridge is null || core is null ||
            _activePage != BrowserPage.Login || _ticketBootstrapCleanupRequired || _ticketBootstrapCleanupInProgress ||
            _automaticTileOpenAttemptedGeneration == _navigationGeneration ||
            !RemoteBridge.IsTrustedUgreenDesktopDocument(core.Source) ||
            !Uri.TryCreate(core.Source, UriKind.Absolute, out var desktop)) return false;

        _automaticTileOpenAttemptedGeneration = _navigationGeneration;
        var desktopOrigin = new Uri(desktop.GetLeftPart(UriPartial.Authority) + "/");
        var configured = TryBuildConfiguredServiceRoot(out var serviceRoot);
        var permit = configured && _settings.AutoOpenShortcut
            ? new PendingAutomaticTileOpen(desktopOrigin, serviceRoot, _navigationGeneration) : null;
        _pendingAutomaticTileOpen = permit;
        StatusText.Text = configured && _settings.AutoOpenShortcut
            ? "Meld je zo nodig aan. De Remote Drive-tegel wordt automatisch geopend zodra het NAS-bureaublad klaar is…"
            : "Meld je aan en open de Remote Drive-tegel één keer. De app onthoudt de gekozen tegel en de aanmelding.";
        DriveLog.Info("Trusted desktop helper started; session preference enabled without reading credentials; shortcut popup remains origin-restricted.");
        _ = ClickConfiguredDesktopTileAsync(permit, configured ? serviceRoot : null, _navigationGeneration);
        return true;
    }

    private async Task ClickConfiguredDesktopTileAsync(PendingAutomaticTileOpen? permit, Uri? serviceRoot, int generation)
    {
        try
        {
            var core = Browser.CoreWebView2;
            if (core is null || _navigationGeneration != generation ||
                !RemoteBridge.IsTrustedUgreenDesktopDocument(core.Source))
            {
                if (permit is not null) ClearAutomaticTilePermit(permit);
                return;
            }

            var result = await core.ExecuteScriptAsync(DesktopAutomation.BuildScript(_settings, serviceRoot));
            if (!string.Equals(result, "true", StringComparison.OrdinalIgnoreCase))
            {
                if (permit is not null) ClearAutomaticTilePermit(permit);
                return;
            }

            // Keep watching through a slow sign-in and delayed SPA tile rendering.
            // Navigation or a successful exact popup invalidates this permit.
            await Task.Delay(TimeSpan.FromMinutes(DesktopAutomation.WaitMinutes));
            if (permit is not null && _pendingAutomaticTileOpen == permit)
            {
                ClearAutomaticTilePermit(permit);
                if (!_exitRequested && _navigationGeneration == generation && _activePage == BrowserPage.Login)
                    StatusText.Text = "De tegel is niet gevonden. Controleer de tegelnaam bij Optioneel of open de Remote Drive-tegel één keer om hem te onthouden.";
            }
        }
        catch (Exception ex)
        {
            if (permit is not null) ClearAutomaticTilePermit(permit);
            DriveLog.Error("Automatic UGREENlink tile attempt failed: " + ex.GetType().Name);
            if (!_exitRequested && _navigationGeneration == generation && _activePage == BrowserPage.Login)
                StatusText.Text = "De tegel kon niet automatisch worden geopend. Klik één keer op Remote Drive op het UGREENlink-bureaublad.";
        }
    }

    private void ClearAutomaticTilePermit(PendingAutomaticTileOpen permit)
    {
        if (_pendingAutomaticTileOpen == permit) _pendingAutomaticTileOpen = null;
    }

    private async Task ProbeRemoteAfterNavigationAsync(int generation)
    {
        if (_remote is null || _bridge is null || !RemoteProbePolicy.CanProbe(
                _activePage == BrowserPage.ServiceRoot, _loginFlowActive, _ticketBootstrapCleanupRequired,
                _ticketBootstrapCleanupInProgress, _probeInProgress)) return;
        _probeInProgress = true;
        RemoteAuthenticationResult result;
        try { result = await Task.Run(_bridge.TryAuthenticateDetailed); }
        finally { _probeInProgress = false; }
        if (generation != _navigationGeneration || _activePage != BrowserPage.ServiceRoot) return;
        RecordRemoteAuthenticationResult(result);
        if (result.Authenticated)
        {
            ShowRemoteBrowser(false);
            _healthNavigationAttempted = false;
            _loginFlowActive = false;
            StatusText.Text = "UGREENlink en de NAS-service zijn verbonden.";
            _lastRemoteProbe = DateTimeOffset.UtcNow;
            await MaybeAutoMountAsync(_connectAndMountRequested);
        }
        else
        {
            if (result.LoginRedirect && !_healthNavigationAttempted && _bridge is not null)
            {
                _healthNavigationAttempted = true;
                _loginFlowActive = true;
                _healthResponseStatus = null;
                StatusText.Text = "UGREENlink-aanmelding openen. Meld je aan in het appvenster; Edge-aanmelding wordt niet overgenomen.";
                Browser.Source = new Uri(_bridge.BuildHealthUrl());
                return;
            }
            if (result.LoginRedirect) _loginFlowActive = true;
            StatusText.Text = result.LoginRedirect
                ? "Meld je aan in het appvenster; Edge-aanmelding wordt niet overgenomen. Klik daarna op Verbinden."
                : RemoteAuthenticationFailureText(result, _connectAndMountRequested);
            if (_settings.AutoStart && !IsVisible) Show();
            if (_connectAndMountRequested && !result.LoginRedirect) await TryMountFromSmbAsync();
        }
        UpdateBackendText();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e) => await ConnectAsync(trySmb: true);

    private async void RemoteConnectButton_Click(object sender, RoutedEventArgs e) => await ConnectAsync(trySmb: false);

    private async Task ConnectAsync(bool trySmb)
    {
        if (_nativeSignInActive || _connectionActionInProgress || _dokanInstance is not null) return;
        _connectionActionInProgress = true;
        SetConfigurationControlsEnabled(false);
        try
        {
            if (_ticketBootstrapCleanupInProgress)
            {
                StatusText.Text = "De veilige shortcut-controle wordt afgerond. Probeer Verbinden zo meteen opnieuw.";
                return;
            }
            _connectAndMountRequested = true;
            _autoMountAttempted = false;
            SaveSettingsFromForm(navigateToRemote: false);
            if (trySmb) { _backends = null; RebuildBackends(); }
            if (trySmb && await TryMountFromSmbAsync(restoreSavedSignIn: true)) return;
            if (trySmb && !string.IsNullOrWhiteSpace(_settings.TailscaleHost))
            {
                await ConnectEmbeddedTailscaleAsync();
                return;
            }
            if (trySmb && !string.IsNullOrWhiteSpace(_settings.SmbPath) &&
                await SmbSignInService.CanContactServerAsync(_settings.SmbPath))
            {
                await SignInAndMountSmbAsync();
                return;
            }
            ShowRemoteBrowser(true);
            if (string.IsNullOrWhiteSpace(_settings.RemoteUrl))
            {
                if (string.IsNullOrWhiteSpace(_settings.NasPortalUrl))
                    throw new InvalidOperationException("De Techbase SMB-share is niet bereikbaar. Activeer je WireGuard-tunnel of controleer het LAN-adres en je Windows SMB-aanmelding.");
                _shortcutDiscoveryActive = true;
                _loginFlowActive = true;
                _healthNavigationAttempted = false;
                Browser.Source = UgreenLinkSetupAddress.Parse(_settings.NasPortalUrl).Address;
                StatusText.Text = "NAS openen. Meld aan in dit venster en klik daarna op de Remote Drive-tegel; het shortcut-adres wordt automatisch onthouden.";
                return;
            }
            ConfigureRemote();
            _healthNavigationAttempted = false;
            _loginFlowActive = false;
            _returningToRoot = false;
            StatusText.Text = "NAS-shortcut openen. Meld je aan in dit appvenster; Edge-aanmelding wordt niet overgenomen.";
            var root = new Uri(_bridge!.BuildServiceRootUrl());
            if (Browser.Source is not null && _bridge.IsConfiguredServiceRoot(Browser.Source))
            {
                StatusText.Text = "Bestaande NAS-verbinding controleren…";
                await ProbeRemoteAfterNavigationAsync(_navigationGeneration);
            }
            else Browser.Source = root;
        }
        catch (Exception ex)
        {
            _connectAndMountRequested = false;
            StatusText.Text = ex.Message;
        }
        finally
        {
            _connectionActionInProgress = false;
            SetConfigurationControlsEnabled(_dokanInstance is null);
        }
    }

    private async Task MountDriveAsync()
    {
        if (_dokanInstance is not null) return;
        RebuildBackends();
        if (_backends is null) throw new InvalidOperationException("Controleer het UGREENlink-adres.");
        await _backends.ProbeSmbAsync(useRecentResult: true);
        if (_dokanInstance is not null) return;
        if (!_backends.SmbReachable && _remote?.IsAuthenticated != true)
            throw new InvalidOperationException("De NAS is nog niet verbonden. Meld aan bij UGREENlink en probeer opnieuw.");

        var letter = (DriveLetterBox.SelectedItem as string ?? _settings.DriveLetter).Trim().TrimEnd('\\');
        var mountPoint = letter + "\\";
        if (DriveInfo.GetDrives().Any(drive => drive.Name.Equals(mountPoint, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{letter} is al in gebruik. Kies een andere schijfletter bij Optioneel.");

        var logger = new NullLogger();
        _dokan = new Dokan(logger);
        var builder = new DokanInstanceBuilder(_dokan)
            .ConfigureLogger(() => logger)
            .ConfigureOptions(options =>
            {
                options.Options = DokanOptions.FixedDrive;
                options.MountPoint = mountPoint;
            });
        _mountedBackend = _backends.PinForMount();
        ShowRemoteBrowser(false);
        var fileSystem = new DriveFileSystem(_mountedBackend);
        _dokanInstance = builder.Build(fileSystem);
        _mountedPoint = mountPoint;
        _autoMountAttempted = true;
        ConnectButton.IsEnabled = false;
        UnmountButton.IsEnabled = true;
        SetConfigurationControlsEnabled(false);
        StatusText.Text = $"Schijf {letter} is gekoppeld; Verkenner wordt geopend. De app blijft in het systeemvak actief.";
        DriveLog.Info($"Drive mount requested; backend={_backends.Describe(_mountedBackend)}; backend-pinned=true.");
        _ = MonitorMountAsync(_dokanInstance, _dokan);
        _ = OpenDriveInExplorerAsync(mountPoint);
        UpdateBackendText();
    }

    private static async Task OpenDriveInExplorerAsync(string mountPoint)
    {
        await Task.Delay(500);
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", mountPoint) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DriveLog.Error("Could not open the mounted drive in Explorer: " + ex.GetType().Name);
        }
    }

    private async Task<bool> TryMountFromSmbAsync(bool restoreSavedSignIn = false)
    {
        if (!_connectAndMountRequested || _backends is null || string.IsNullOrWhiteSpace(_settings.SmbPath)) return false;
        await _backends.ProbeSmbAsync(useRecentResult: true);
        if (!_backends.SmbReachable && restoreSavedSignIn && !_savedSmbSignInAttempted.Contains(_settings.SmbPath) &&
            await SmbSignInService.CanContactServerAsync(_settings.SmbPath))
        {
            var root = _settings.SmbPath;
            _savedSmbSignInAttempted.Add(root);
            try
            {
                var result = await Task.Run(() => _smbSignIn.Restore(root));
                if (!root.Equals(_settings.SmbPath, StringComparison.OrdinalIgnoreCase) || _dokanInstance is not null) return false;
                if (result?.Success == true)
                {
                    _backends = null; // Discard any probe begun before the new SMB session.
                    RebuildBackends();
                    await _backends!.ProbeSmbAsync();
                }
                else if (result is not null) StatusText.Text = result.Message;
            }
            catch { StatusText.Text = "De onthouden NAS-aanmelding kon niet worden gebruikt. Klik op Aanmelden bij NAS."; }
        }
        if (_backends?.SmbReachable != true) return false;
        try
        {
            await MountDriveAsync();
            _connectAndMountRequested = false;
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Koppelen is mislukt: " + ex.Message;
            DriveLog.Error("Mount failed: " + ex.GetType().Name);
            DisposeMountObjects();
            return false;
        }
    }

    private async Task MonitorMountAsync(DokanInstance instance, Dokan dokan)
    {
        try { await instance.WaitForFileSystemClosedAsync(uint.MaxValue); }
        catch (Exception ex) { DriveLog.Error("Dokan mount ended: " + ex.GetType().Name); }
        await Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_dokanInstance, instance)) return;
            DisposeMountObjects();
            ConnectButton.IsEnabled = true;
            UnmountButton.IsEnabled = false;
            SetConfigurationControlsEnabled(true);
            _mountedPoint = null;
            StatusText.Text = "De schijf is ontkoppeld.";
            UpdateBackendText();
        });
    }

    private void UnmountButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dokan is null || _mountedPoint is null) return;
        StatusText.Text = "Schijf wordt ontkoppeld…";
        _dokan.RemoveMountPoint(_mountedPoint);
    }

    private async void ForgetLoginButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = WpfMessageBox.Show(this,
            "Dit verwijdert de lokale aanmeldsessie en door deze appbrowser bewaarde inloggegevens. Je NAS-bestanden en app-instellingen blijven staan.",
            "Lokale aanmelding wissen", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK || Browser.CoreWebView2 is null) return;
        try
        {
            _connectAndMountRequested = false;
            _pendingAutomaticTileOpen = null;
            await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.AllSite | CoreWebView2BrowsingDataKinds.PasswordAutosave |
                CoreWebView2BrowsingDataKinds.GeneralAutofill);
            Browser.Reload();
            StatusText.Text = "Lokale aanmelding en bewaarde inloggegevens gewist. Meld opnieuw aan om de remote schijf te gebruiken.";
            DriveLog.Info("Local browser sign-in/site storage and saved credentials cleared by user.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "De lokale aanmelding kon niet volledig worden gewist. Probeer opnieuw.";
            DriveLog.Error("Local sign-in cleanup failed: " + ex.GetType().Name);
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (_nativeSignInActive || _mountedBackend is SmbBackend or EmbeddedTailscaleBackend) { UpdateBackendText(); return; }
        if (_backends is not null)
            _ = _backends.ProbeSmbAsync(useRecentResult: true);

        if (RemoteProbePolicy.CanProbe(_activePage == BrowserPage.ServiceRoot, _loginFlowActive,
                _ticketBootstrapCleanupRequired, _ticketBootstrapCleanupInProgress, _probeInProgress) &&
            _remote is not null && _bridge is not null && DateTimeOffset.UtcNow - _lastRemoteProbe >= TimeSpan.FromSeconds(12))
        {
            _lastRemoteProbe = DateTimeOffset.UtcNow;
            var generation = _navigationGeneration;
            _probeInProgress = true;
            RemoteAuthenticationResult result;
            try { result = await Task.Run(_bridge.TryAuthenticateDetailed); }
            finally { _probeInProgress = false; }
            if (generation != _navigationGeneration || _activePage != BrowserPage.ServiceRoot || _loginFlowActive) return;
            RecordRemoteAuthenticationResult(result);
            if (!result.Authenticated && _settings.AutoStart && _dokanInstance is null && Browser.Source is not null && !IsVisible)
                Show();
            if (result.Authenticated) await MaybeAutoMountAsync(_connectAndMountRequested);
            else if (result.LoginRedirect && !_healthNavigationAttempted)
            {
                _healthNavigationAttempted = true;
                _loginFlowActive = true;
                Browser.Source = new Uri(_bridge.BuildHealthUrl());
            }
            else if (result.LoginRedirect)
            {
                _loginFlowActive = true;
                StatusText.Text = "Meld aan in dit appvenster en open daarna de Remote Drive-tegel op het UGREENlink-bureaublad.";
            }
            else if (IsVisible) StatusText.Text = RemoteAuthenticationFailureText(result, _connectAndMountRequested);
        }
        UpdateBackendText();
    }

    private void RecordRemoteAuthenticationResult(RemoteAuthenticationResult result)
    {
        if (result.Authenticated)
        {
            if (_lastRemoteFailureDiagnostic is not null)
                DriveLog.Info("Remote authentication probe succeeded after a previous failure.");
            _lastRemoteFailureDiagnostic = null;
            return;
        }

        if (_lastRemoteFailureDiagnostic == result.DiagnosticCode) return;
        DriveLog.Error($"Remote authentication probe failed ({result.DiagnosticCode}).");
        _lastRemoteFailureDiagnostic = result.DiagnosticCode;
    }

    private static string RemoteAuthenticationFailureText(RemoteAuthenticationResult result, bool mountRequested)
    {
        if (result.LoginRedirect)
            return mountRequested
                ? "UGREENlink stuurde door naar aanmelden. Meld aan in dit appvenster en open daarna de Remote Drive-tegel; de schijf wordt daarna opnieuw geprobeerd."
                : "UGREENlink stuurde door naar aanmelden. Meld aan in dit appvenster en open daarna de Remote Drive-tegel.";

        if (result.FailureClass == "InvalidRootStatResponse")
            return "De NAS antwoordde met HTTP 200, maar gaf geen geldige metadata voor de Techbase-root terug; aanmelden is niet bevestigd.";

        if (result.HttpStatus is int status)
            return $"De NAS-API antwoordde met HTTP {status}; dit is geen gedetecteerde UGREENlink-aanmeldredirect.";

        if (result.FailureClass is string failureClass)
            return $"De remote-controle mislukte ({failureClass}); de aanmeldstatus kon niet worden bevestigd.";

        return "De remote-controle mislukte zonder bruikbare status; de aanmeldstatus kon niet worden bevestigd.";
    }

    private async Task MaybeAutoMountAsync(bool requested = false)
    {
        if (_nativeSignInActive || (!_settings.AutoStart && !requested) || _autoMountAttempted || _dokanInstance is not null) return;
        _autoMountAttempted = true;
        try
        {
            await MountDriveAsync();
            _connectAndMountRequested = false;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Koppelen is mislukt: " + ex.Message;
            DriveLog.Error("Mount failed: " + ex.GetType().Name);
            DisposeMountObjects();
            if (requested) _connectAndMountRequested = false;
        }
    }

    private void SaveSettingsFromForm(bool navigateToRemote = true)
    {
        if (_dokanInstance is not null)
            throw new InvalidOperationException("Ontkoppel de schijf voordat je instellingen wijzigt.");
        var urlText = RemoteUrlBox.Text.Trim();
        var smbPath = SmbPathBox.Text.Trim();
        var tailHost = string.IsNullOrWhiteSpace(TailscaleHostBox.Text) ? "" : EmbeddedTailscaleBackend.ValidateHost(TailscaleHostBox.Text);
        var setupTarget = UgreenLinkSetupAddress.ParseConnection(urlText,
            string.IsNullOrWhiteSpace(smbPath) && tailHost.Length > 0 ? @"\\" + tailHost + @"\Techbase" : smbPath);
        var drive = DriveLetterBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(drive)) throw new InvalidOperationException("Kies een driveletter.");
        var tileName = ShortcutTileNameBox.Text.Trim();
        if (DesktopTileSelection.Validate("", tileName) is null)
            throw new InvalidOperationException("Vul de exacte NAS-tegelnaam in (maximaal 128 tekens).");
        var sameShortcut = setupTarget?.Kind == UgreenLinkSetupKind.Shortcut &&
            setupTarget.Address.AbsoluteUri.Equals(_settings.RemoteUrl, StringComparison.OrdinalIgnoreCase);
        var sameTileName = tileName.Equals(_settings.ShortcutTileName, StringComparison.OrdinalIgnoreCase);

        _settings = new DriveSettings
        {
            RemoteUrl = setupTarget?.Kind == UgreenLinkSetupKind.Shortcut ? setupTarget.Address.AbsoluteUri : "",
            NasPortalUrl = setupTarget?.Kind == UgreenLinkSetupKind.NasPortal ? setupTarget.Address.AbsoluteUri :
                sameShortcut ? _settings.NasPortalUrl : "",
            SmbPath = smbPath,
            TailscaleHost = tailHost,
            DriveLetter = drive,
            AutoStart = StartupBox.IsChecked == true,
            RememberLogin = RememberLoginBox.IsChecked == true,
            AutoOpenShortcut = AutoOpenShortcutBox.IsChecked == true,
            ShortcutTileName = tileName,
            ShortcutTileId = sameShortcut && sameTileName ? _settings.ShortcutTileId : ""
        };
        SettingsStore.Save(_settings);
        if (Browser.CoreWebView2 is { } core) core.Profile.IsPasswordAutosaveEnabled = _settings.RememberLogin;
        UpdateStartupRegistration(_settings.AutoStart);
        ConfigureRemote();
        if (navigateToRemote && _settings.AutoStart && !_settings.RemoteUrl.Equals(Browser.Source?.AbsoluteUri, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_settings.RemoteUrl))
        {
            Browser.Source = ValidateRemoteUrl(_settings.RemoteUrl);
        }
        RebuildBackends();
        StatusText.Text = "Instellingen opgeslagen.";
    }

    private bool TryConfigureFromSettings()
    {
        if (string.IsNullOrWhiteSpace(_settings.RemoteUrl)) return false;
        try
        {
            _ = ValidateRemoteUrl(_settings.RemoteUrl);
            ConfigureRemote();
            return true;
        }
        catch { return false; }
    }

    private bool TryStartSavedNasPortalDiscovery()
    {
        if (string.IsNullOrWhiteSpace(_settings.NasPortalUrl)) return false;
        try
        {
            var portal = UgreenLinkSetupAddress.Parse(_settings.NasPortalUrl);
            if (portal.Kind != UgreenLinkSetupKind.NasPortal) return false;
            ShowRemoteBrowser(true);
            _shortcutDiscoveryActive = true;
            _loginFlowActive = true;
            Browser.Source = portal.Address;
            StatusText.Text = "Meld aan bij de NAS in dit venster en open daarna de Remote Drive-tegel. Het app-adres wordt automatisch onthouden.";
            return true;
        }
        catch (Exception ex)
        {
            DriveLog.Error("Saved UGREENlink NAS portal address is invalid: " + ex.GetType().Name);
            return false;
        }
    }

    private void ConfigureRemote()
    {
        if (_bridge is null) return;
        if (string.IsNullOrWhiteSpace(_settings.RemoteUrl))
        {
            _bridge.ClearConfiguration();
            _remote ??= new RemoteBackend(_bridge);
            UpdateBackendText();
            return;
        }
        var uri = ValidateRemoteUrl(_settings.RemoteUrl);
        _bridge.Configure(uri);
        _remote ??= new RemoteBackend(_bridge);
    }

    private void RebuildBackends()
    {
        if (_remote is null && _bridge is not null && !string.IsNullOrWhiteSpace(_settings.RemoteUrl)) _remote = new RemoteBackend(_bridge);
        if (_remote is null) return;
        if (_backends is not null && ReferenceEquals(_backends.Remote, _remote) &&
            string.Equals(_backendSmbPath, _settings.SmbPath, StringComparison.OrdinalIgnoreCase)) return;
        var smb = string.IsNullOrWhiteSpace(_settings.SmbPath) ? null : new SmbBackend(_settings.SmbPath);
        _backends = new BackendSelector(_remote, smb);
        _backendSmbPath = _settings.SmbPath;
    }

    private void UpdateBackendText()
    {
        BackendText.Text = "Backend: " + (_backends?.Mode ?? "geen");
    }

    private void ShowRemoteBrowser(bool visible)
    {
        BrowserHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (_ugreenLoginWindow is null) return;
        if (visible) { if (!_ugreenLoginWindow.IsVisible) _ugreenLoginWindow.Show(); _ugreenLoginWindow.Activate(); }
        else _ugreenLoginWindow.Hide();
    }

    private void CreateUgreenLoginPopup()
    {
        if (_ugreenLoginWindow is not null) return;
        if (BrowserHost.Parent is System.Windows.Controls.Panel parent) parent.Children.Remove(BrowserHost);
        _ugreenLoginWindow = new Window
        {
            Title = "Aanmelden bij UGREENlink", Owner = this, Width = 850, Height = 650,
            MinWidth = 650, MinHeight = 450, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, Content = BrowserHost
        };
        _ugreenLoginWindow.Closing += (_, e) => { if (!_exitRequested) { e.Cancel = true; _ugreenLoginWindow.Hide(); } };
    }

    private async void ConnectTailscaleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_nativeSignInActive || _connectionActionInProgress || _dokanInstance is not null) return;
        try { SaveSettingsFromForm(navigateToRemote: false); await ConnectEmbeddedTailscaleAsync(); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async Task ConnectEmbeddedTailscaleAsync()
    {
        if (_nativeSignInActive || _dokanInstance is not null) return;
        _nativeSignInActive = true;
        SetConfigurationControlsEnabled(false);
        ShowRemoteBrowser(false);
        try
        {
            var host = EmbeddedTailscaleBackend.ValidateHost(_settings.TailscaleHost);
            if (_embeddedTailscale?.Host != host || !_embeddedTailscale.IsRunning)
            {
                _embeddedTailscale?.Dispose(); _embeddedTailscale = null;
                StatusText.Text = "Ingebouwde Tailscale-verbinding starten…";
                _embeddedTailscale = await Task.Run(() => new EmbeddedTailscaleBackend(host));
            }
            var backend = _embeddedTailscale;
            var status = await Task.Run(backend.GetStatus);
            for (var attempt = 0; attempt < 10 && status.State is "NoState" or "Starting"; attempt++)
            {
                await Task.Delay(500); status = await Task.Run(backend.GetStatus);
            }
            if (status.State != "Running")
            {
                if (status.State == "NeedsMachineAuth") throw new InvalidOperationException("De Tailscale-beheerder moet deze app nog toestemming geven.");
                var authorization = new TailscaleKeyWindow(backend) { Owner = this };
                if (authorization.ShowDialog() != true) { _connectAndMountRequested = false; StatusText.Text = "De Tailscale-instelling is nog niet afgerond."; return; }
                status = await Task.Run(backend.GetStatus);
            }
            if (!status.Ready) throw new InvalidOperationException("Tailscale is verbonden, maar de ingestelde NAS is niet zichtbaar. Controleer de NAS-naam en toegangsregels in het juiste Tailscale-netwerk.");
            var signIn = new SmbSignInService(new WindowsSmbCredentialStore(), backend);
            if (!backend.IsAuthenticated && _savedSmbSignInAttempted.Add(backend.ShareRoot))
            {
                try { await Task.Run(() => signIn.Restore(backend.ShareRoot)); }
                catch { StatusText.Text = "De NAS-aanmelding kon niet worden hersteld. Meld aan in de pop-up."; }
            }
            string? warning = null;
            if (!backend.IsAuthenticated)
            {
                var dialog = new NasSignInWindow(backend.ShareRoot, signIn) { Owner = this };
                if (dialog.ShowDialog() != true) { _connectAndMountRequested = false; StatusText.Text = "NAS-aanmelding geannuleerd."; return; }
                warning = dialog.SaveWarning;
            }
            _backends = new BackendSelector(_remote!, backend, () => _remote?.IsAuthenticated == true, () => backend.IsAuthenticated);
            _backendSmbPath = _settings.SmbPath;
            await MountDriveAsync(); _connectAndMountRequested = false;
            try { status = await Task.Run(backend.GetStatus); } catch { status = status with { ConnectionType = "unknown" }; }
            var route = status.ConnectionType switch { "direct" => "rechtstreeks", "relay" => "via Tailscale-relay", "peer-relay" => "via peer-relay", _ => "route nog niet vastgesteld" };
            StatusText.Text += " Tailscale: " + route + ".";
            if (warning is not null) StatusText.Text += " " + warning;
        }
        catch (Exception ex)
        {
            _connectAndMountRequested = false;
            StatusText.Text = ex is DriveApiException ? "De ingebouwde Tailscale/NAS-verbinding kon niet worden afgerond. Controleer de toestemming en SMB op de NAS." : ex.Message;
            DriveLog.Error("Embedded Tailscale connection failed: " + ex.GetType().Name);
        }
        finally { _nativeSignInActive = false; SetConfigurationControlsEnabled(_dokanInstance is null); }
    }

    private async void ForgetTailscaleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_nativeSignInActive || _connectionActionInProgress || _dokanInstance is not null) return;
        _nativeSignInActive = true; SetConfigurationControlsEnabled(false);
        try
        {
            if (_embeddedTailscale is null) _embeddedTailscale = await Task.Run(() => new EmbeddedTailscaleBackend(EmbeddedTailscaleBackend.ValidateHost(_settings.TailscaleHost)));
            await Task.Run(_embeddedTailscale.Logout);
            _embeddedTailscale.Dispose(); _embeddedTailscale = null;
            StatusText.Text = "De eigen Tailscale-toestemming van deze app is ingetrokken. Andere Tailscale-apps en je NAS zijn niet gewijzigd.";
        }
        catch { StatusText.Text = "De Tailscale-toestemming kon niet worden gewist. Controleer de verbinding en probeer opnieuw."; }
        finally { _nativeSignInActive = false; SetConfigurationControlsEnabled(true); }
    }

    private async void SmbSignInButton_Click(object sender, RoutedEventArgs e)
    {
        if (_nativeSignInActive || _dokanInstance is not null) return;
        try
        {
            SaveSettingsFromForm(navigateToRemote: false);
            _connectAndMountRequested = true;
            await SignInAndMountSmbAsync();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async Task SignInAndMountSmbAsync()
    {
        if (_nativeSignInActive || _dokanInstance is not null) return;
        _nativeSignInActive = true;
        SetConfigurationControlsEnabled(false);
        ShowRemoteBrowser(false);
        try
        {
            var dialog = new NasSignInWindow(_settings.SmbPath, _smbSignIn) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                _connectAndMountRequested = false;
                StatusText.Text = "NAS-aanmelding geannuleerd. Klik opnieuw op Verbinden om een verbinding te kiezen.";
                return;
            }
            _savedSmbSignInAttempted.Add(_settings.SmbPath);
            _backends = null;
            RebuildBackends();
            if (!await TryMountFromSmbAsync())
                StatusText.Text = "Aangemeld, maar de schijf kon niet worden geopend. Controleer de Techbase-rechten en de driver.";
            if (dialog.SaveWarning is not null) StatusText.Text += " " + dialog.SaveWarning;
        }
        finally
        {
            _nativeSignInActive = false;
            SetConfigurationControlsEnabled(_dokanInstance is null);
        }
    }

    private void ForgetSmbButton_Click(object sender, RoutedEventArgs e)
    {
        if (_nativeSignInActive || _dokanInstance is not null) return;
        try
        {
            _smbSignIn.Forget(SmbPathBox.Text);
            StatusText.Text = "De door deze app onthouden NAS-aanmelding is gewist. Een actieve Windows SMB-sessie blijft werken tot Windows die sluit.";
        }
        catch { StatusText.Text = "De NAS-aanmelding kon niet worden gewist. Controleer het Techbase SMB-adres en probeer opnieuw."; }
    }

    private void SetConfigurationControlsEnabled(bool enabled)
    {
        RemoteUrlBox.IsEnabled = enabled;
        SmbPathBox.IsEnabled = enabled;
        TailscaleHostBox.IsEnabled = enabled;
        ConnectTailscaleButton.IsEnabled = enabled;
        ForgetTailscaleButton.IsEnabled = enabled;
        SmbSignInButton.IsEnabled = enabled;
        ForgetSmbButton.IsEnabled = enabled;
        DriveLetterBox.IsEnabled = enabled;
        StartupBox.IsEnabled = enabled;
        ConnectButton.IsEnabled = enabled;
        RemoteConnectButton.IsEnabled = enabled;
        ForgetLoginButton.IsEnabled = enabled;
        RememberLoginBox.IsEnabled = enabled;
        AutoOpenShortcutBox.IsEnabled = enabled;
        ShortcutTileNameBox.IsEnabled = enabled;
    }

    private static Uri ValidateRemoteUrl(string value) => UgreenLinkAddress.Validate(value);

    private static void UpdateStartupRegistration(bool enabled)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (enabled)
        {
            var executable = Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Kan het pad van de client niet bepalen.");
            runKey.SetValue("UGREENRemoteDrive", $"\"{executable}\" --minimized");
        }
        else
        {
            runKey.DeleteValue("UGREENRemoteDrive", false);
        }
    }

    private WinForms.NotifyIcon CreateTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Openen", null, (_, _) => RestoreWindow());
        menu.Items.Add("Afsluiten", null, async (_, _) => await ExitFromTrayAsync());
        var icon = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "UGREEN Remote Drive",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => RestoreWindow();
        return icon;
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested) return;
        e.Cancel = true;
        Hide();
    }

    private async Task ExitFromTrayAsync()
    {
        if (_dokan is not null && _mountedPoint is not null)
            _dokan.RemoveMountPoint(_mountedPoint);
        if (_dokanInstance is not null)
        {
            try { await _dokanInstance.WaitForFileSystemClosedAsync(5000); }
            catch { }
        }
        DisposeMountObjects();
        UpdateStartupRegistration(_settings.AutoStart);
        _statusTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Browser.Dispose();
        _embeddedTailscale?.Dispose();
        _exitRequested = true;
        _ugreenLoginWindow?.Close();
        Close();
    }

    private void DisposeMountObjects()
    {
        try { _dokanInstance?.Dispose(); } catch { }
        try { _dokan?.Dispose(); } catch { }
        _dokanInstance = null;
        _dokan = null;
        var mountedBackend = _mountedBackend;
        _mountedBackend = null;
        _backends?.ReleaseMount(mountedBackend);
    }
}
