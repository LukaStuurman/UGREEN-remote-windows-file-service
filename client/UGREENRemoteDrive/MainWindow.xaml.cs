using System.ComponentModel;
using System.Diagnostics;
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
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly WinForms.NotifyIcon _trayIcon;
    private DriveSettings _settings = new();
    private RemoteBridge? _bridge;
    private RemoteBackend? _remote;
    private BackendSelector? _backends;
    private Dokan? _dokan;
    private DokanInstance? _dokanInstance;
    private DateTimeOffset _lastRemoteProbe;
    private string? _mountedPoint;
    private bool _exitRequested;
    private bool _autoMountAttempted;

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
        _settings = SettingsStore.Load();
        RemoteUrlBox.Text = _settings.RemoteUrl;
        SmbPathBox.Text = _settings.SmbPath;
        if (DriveLetterBox.Items.Contains(_settings.DriveLetter)) DriveLetterBox.SelectedItem = _settings.DriveLetter;
        StartupBox.IsChecked = _settings.AutoStart;
        TokenHint.Text = string.IsNullOrEmpty(_settings.Token)
            ? "Genereer een token van minstens 32 tekens en voer die ook in de Docker-projectinstellingen in."
            : "Een token is opgeslagen. Laat dit veld leeg om het opgeslagen token te behouden.";

        try
        {
            Directory.CreateDirectory(SettingsStore.AppDirectory);
            var profile = Path.Combine(SettingsStore.AppDirectory, "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, profile);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _bridge = new RemoteBridge(Browser);
            await _bridge.InitializeAsync();
            _remote = new RemoteBackend(_bridge);
            Browser.NavigationCompleted += Browser_NavigationCompleted;
        }
        catch (Exception ex)
        {
            StatusText.Text = "WebView2 kon niet starten. Installeer zo nodig de officiële Microsoft Edge WebView2 Runtime.";
            DriveLog.Error("WebView2 initialization failed: " + ex.GetType().Name);
            return;
        }

        if (TryConfigureFromSettings())
        {
            Browser.Source = ValidateRemoteUrl(_settings.RemoteUrl);
            StatusText.Text = "UGREENlink openen. Meld aan in het venster hieronder.";
        }
        RebuildBackends();
        _statusTimer.Start();
        if (Environment.GetCommandLineArgs().Contains("--minimized", StringComparer.OrdinalIgnoreCase)) Hide();
    }

    private void Browser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _ = ProbeRemoteAfterNavigationAsync();
    }

    private async Task ProbeRemoteAfterNavigationAsync()
    {
        if (_remote is null) return;
        var authenticated = await Task.Run(_remote.Authenticate);
        if (authenticated)
        {
            StatusText.Text = "UGREENlink en de NAS-service zijn verbonden.";
            _lastRemoteProbe = DateTimeOffset.UtcNow;
            await MaybeAutoMountAsync();
        }
        else
        {
            StatusText.Text = "Meld je aan bij UGREENlink in het browservenster.";
            if (_settings.AutoStart && !IsVisible) Show();
        }
        UpdateBackendText();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveSettingsFromForm();
            WpfMessageBox.Show(this, "Instellingen opgeslagen.", "UGREEN Remote Drive", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this, ex.Message, "Instellingen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveSettingsFromForm();
            if (string.IsNullOrWhiteSpace(_settings.RemoteUrl)) throw new InvalidOperationException("Vul eerst de ugapp.link-snelkoppeling in.");
            var target = ValidateRemoteUrl(_settings.RemoteUrl);
            ConfigureRemote();
            if (Browser.Source is null || !SameOrigin(Browser.Source, target))
            {
                StatusText.Text = "UGREENlink openen. Meld aan in het browservenster hieronder.";
                Browser.Source = target;
            }
            else
            {
                await ProbeRemoteAfterNavigationAsync();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void MountButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_dokanInstance is not null) return;
            SaveSettingsFromForm();
            RebuildBackends();
            if (_backends is null) throw new InvalidOperationException("Controleer de instellingen.");
            await _backends.ProbeSmbAsync();
            var letter = (DriveLetterBox.SelectedItem as string ?? _settings.DriveLetter).Trim().TrimEnd('\\');
            var mountPoint = letter + "\\";
            if (DriveInfo.GetDrives().Any(drive => drive.Name.Equals(mountPoint, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"{letter} is al in gebruik. Kies een andere driveletter.");

            var logger = new NullLogger();
            _dokan = new Dokan(logger);
            var builder = new DokanInstanceBuilder(_dokan)
                .ConfigureLogger(() => logger)
                .ConfigureOptions(options =>
                {
                    options.Options = DokanOptions.FixedDrive;
                    options.MountPoint = mountPoint;
                });
            _dokanInstance = builder.Build(new DriveFileSystem(_backends));
            _mountedPoint = mountPoint;
            MountButton.IsEnabled = false;
            UnmountButton.IsEnabled = true;
            SetConfigurationControlsEnabled(false);
            StatusText.Text = $"Schijf {letter} is gekoppeld. De app blijft in het systeemvak actief.";
            DriveLog.Info("Drive mount requested.");
            _ = MonitorMountAsync(_dokanInstance, _dokan);
        }
        catch (Exception ex)
        {
            DisposeMountObjects();
            StatusText.Text = "Koppelen is mislukt: " + ex.Message;
            DriveLog.Error("Mount failed: " + ex.GetType().Name);
        }
        UpdateBackendText();
    }

    private async Task MonitorMountAsync(DokanInstance instance, Dokan dokan)
    {
        try { await instance.WaitForFileSystemClosedAsync(uint.MaxValue); }
        catch (Exception ex) { DriveLog.Error("Dokan mount ended: " + ex.GetType().Name); }
        await Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_dokanInstance, instance)) return;
            DisposeMountObjects();
            MountButton.IsEnabled = true;
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

    private void ForgetLoginButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = WpfMessageBox.Show(this,
            "Dit verwijdert de lokale WebView2-aanmeldsessie voor UGREEN Remote Drive. Je NAS-bestanden en instellingen blijven staan.",
            "Lokale aanmelding wissen", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK || Browser.CoreWebView2 is null) return;
        Browser.CoreWebView2.CookieManager.DeleteAllCookies();
        Browser.Reload();
        StatusText.Text = "Lokale UGREENlink-cookies gewist. Meld opnieuw aan om de remote schijf te gebruiken.";
        DriveLog.Info("Local WebView sign-in cookies cleared by user.");
    }

    private async Task RefreshStatusAsync()
    {
        if (_backends is not null)
            await _backends.ProbeSmbAsync();

        if (_remote is not null && DateTimeOffset.UtcNow - _lastRemoteProbe >= TimeSpan.FromSeconds(12))
        {
            _lastRemoteProbe = DateTimeOffset.UtcNow;
            var authenticated = await Task.Run(_remote.Authenticate);
            if (!authenticated && _settings.AutoStart && _dokanInstance is null && Browser.Source is not null && !IsVisible)
                Show();
            if (authenticated) await MaybeAutoMountAsync();
        }
        UpdateBackendText();
    }

    private async Task MaybeAutoMountAsync()
    {
        if (!_settings.AutoStart || _autoMountAttempted || _dokanInstance is not null) return;
        _autoMountAttempted = true;
        await Dispatcher.InvokeAsync(() => MountButton_Click(this, new RoutedEventArgs()));
    }

    private void SaveSettingsFromForm()
    {
        if (_dokanInstance is not null)
            throw new InvalidOperationException("Ontkoppel de schijf voordat je instellingen wijzigt.");
        var urlText = RemoteUrlBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(urlText)) _ = ValidateRemoteUrl(urlText);
        var token = string.IsNullOrWhiteSpace(urlText) ? "" :
            string.IsNullOrWhiteSpace(TokenBox.Password) ? _settings.Token : TokenBox.Password.Trim();
        if (!string.IsNullOrWhiteSpace(urlText) && token.Length < 32)
            throw new InvalidOperationException("Het toegangstoken moet minstens 32 tekens lang zijn.");
        var smbPath = SmbPathBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(smbPath) && !smbPath.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidOperationException("Gebruik voor SMB een UNC-pad, bijvoorbeeld \\\\NASNAAM\\Share.");
        var drive = DriveLetterBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(drive)) throw new InvalidOperationException("Kies een driveletter.");

        _settings = new DriveSettings
        {
            RemoteUrl = urlText,
            SmbPath = smbPath,
            DriveLetter = drive,
            Token = token,
            AutoStart = StartupBox.IsChecked == true
        };
        SettingsStore.Save(_settings);
        UpdateStartupRegistration(_settings.AutoStart);
        TokenBox.Clear();
        TokenHint.Text = "Een token is opgeslagen. Laat dit veld leeg om het opgeslagen token te behouden.";
        ConfigureRemote();
        if (_settings.AutoStart && !_settings.RemoteUrl.Equals(Browser.Source?.AbsoluteUri, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_settings.RemoteUrl))
            Browser.Source = ValidateRemoteUrl(_settings.RemoteUrl);
        RebuildBackends();
        StatusText.Text = "Instellingen en versleutelde token zijn opgeslagen.";
    }

    private bool TryConfigureFromSettings()
    {
        if (string.IsNullOrWhiteSpace(_settings.RemoteUrl) || _settings.Token.Length < 32) return false;
        try
        {
            _ = ValidateRemoteUrl(_settings.RemoteUrl);
            ConfigureRemote();
            return true;
        }
        catch { return false; }
    }

    private void ConfigureRemote()
    {
        if (_bridge is null) return;
        if (string.IsNullOrWhiteSpace(_settings.RemoteUrl) || _settings.Token.Length < 32)
        {
            _bridge.ClearConfiguration();
            return;
        }
        var uri = ValidateRemoteUrl(_settings.RemoteUrl);
        _bridge.Configure(uri, _settings.Token);
        _remote ??= new RemoteBackend(_bridge);
    }

    private void RebuildBackends()
    {
        if (_remote is null && _bridge is not null && _settings.Token.Length >= 32) _remote = new RemoteBackend(_bridge);
        if (_remote is null) return;
        var smb = string.IsNullOrWhiteSpace(_settings.SmbPath) ? null : new SmbBackend(_settings.SmbPath);
        _backends = new BackendSelector(_remote, smb);
    }

    private void UpdateBackendText()
    {
        BackendText.Text = "Backend: " + (_backends?.Mode ?? "geen");
    }

    private void SetConfigurationControlsEnabled(bool enabled)
    {
        RemoteUrlBox.IsEnabled = enabled;
        TokenBox.IsEnabled = enabled;
        SmbPathBox.IsEnabled = enabled;
        DriveLetterBox.IsEnabled = enabled;
        StartupBox.IsEnabled = enabled;
    }

    private static Uri ValidateRemoteUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.EndsWith(".ugapp.link", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Vul de HTTPS-URL in van de UGREENlink-desktopshortcut voor de container (domein moet eindigen op .ugapp.link).");
        return uri;
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        a.GetLeftPart(UriPartial.Authority).Equals(b.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

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
        _exitRequested = true;
        Close();
    }

    private void DisposeMountObjects()
    {
        try { _dokanInstance?.Dispose(); } catch { }
        try { _dokan?.Dispose(); } catch { }
        _dokanInstance = null;
        _dokan = null;
    }
}
