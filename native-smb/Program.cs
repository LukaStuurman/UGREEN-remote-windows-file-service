using System.Diagnostics;
using System.Drawing;
using System.Security;
using System.Text.Json;
using System.Windows.Forms;

namespace UGREENRemoteDrive.Native;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new NativeWindow());
    }
}

internal sealed class NativeWindow : Form
{
    private const string SettingsDirectoryName = "UGREEN Remote Drive Native";
    private readonly ManagedTailnet _tailnet = new();
    private readonly TextBox _lan = new(), _host = new(), _nasUser = new(), _nasPassword = new(), _authKey = new();
    private readonly ComboBox _drive = new();
    private readonly Label _status = new();
    private readonly Button _lanConnect = new(), _remoteConnect = new(), _disconnect = new(), _disableTailnet = new();
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        SettingsDirectoryName, "settings.json");
    private bool _busy;

    public NativeWindow()
    {
        Text = "Techbase | Remote Drive — Native SMB";
        Size = new Size(800, 710);
        MinimumSize = new Size(670, 660);
        BackColor = Color.FromArgb(246, 248, 252);
        ForeColor = Color.FromArgb(23, 37, 57);
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1,
            RowCount = 3, Padding = new Padding(22), AutoScroll = true };
        Controls.Add(root);
        var heading = new Label { Text = "TECHBASE  /  NATIVE SMB", Font = new Font("Segoe UI", 21, FontStyle.Bold),
            Height = 64, Dock = DockStyle.Top, ForeColor = Color.FromArgb(255, 90, 0) };
        root.Controls.Add(heading);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2,
            Padding = new Padding(12), BackColor = Color.White };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.Controls.Add(body);
        Row(body, "Schijfletter", _drive);
        for (char c = 'D'; c <= 'Z'; c++) _drive.Items.Add(c + ":");
        _drive.DropDownStyle = ComboBoxStyle.DropDownList;
        _drive.SelectedItem = "T:";
        Row(body, "LAN / kantoor", _lan);
        _lan.PlaceholderText = @"\\NASNAAM\Techbase";
        Row(body, "Tailscale NAS", _host);
        _host.PlaceholderText = "100.100.100.100 of nas.tailnet.ts.net";
        Row(body, "Eenmalige Tailscale-sleutel", _authKey);
        _authKey.PlaceholderText = "Alleen voor eerste registratie (niet opgeslagen)";
        _authKey.UseSystemPasswordChar = true;
        Row(body, "Lokale UGOS-gebruiker", _nasUser);
        _nasUser.PlaceholderText = "Geen UGREENlink-account; gewone SMB-gebruiker";
        Row(body, "SMB-wachtwoord", _nasPassword);
        _nasPassword.UseSystemPasswordChar = true;
        _nasPassword.PlaceholderText = "Leeg laten voor bestaande Windows SMB-aanmelding";

        var details = new Label {
            Text = "Deze editie koppelt Techbase via de eigen SMB-client van Windows. " +
                   "Geen Dokany en geen aparte Windows Tailscale-installatie. " +
                   "De installer beheert een privé-Tailscale-daemon met Wintun. " +
                   "Voor de eerste Tailscale-registratie is eenmalig administrator-toegang nodig.",
            AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(680, 0),
            ForeColor = Color.FromArgb(90, 104, 123), Padding = new Padding(0, 12, 0, 12)
        };
        body.Controls.Add(details, 0, body.RowCount);
        body.SetColumnSpan(details, 2);
        body.RowCount++;

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true };
        _lanConnect.Text = "LAN SMB verbinden"; _remoteConnect.Text = "Ingebouwde Tailscale + SMB";
        _disconnect.Text = "Schijf ontkoppelen"; _disableTailnet.Text = "Tailscale stoppen";
        foreach (var button in new[] { _lanConnect, _remoteConnect, _disconnect, _disableTailnet })
        {
            button.AutoSize = true; button.Padding = new Padding(10, 7, 10, 7);
            button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 0;
            button.BackColor = button == _remoteConnect ? Color.FromArgb(255, 90, 0) : Color.FromArgb(230, 235, 242);
            button.ForeColor = button == _remoteConnect ? Color.White : ForeColor;
            buttons.Controls.Add(button);
        }
        body.Controls.Add(buttons, 0, body.RowCount);
        body.SetColumnSpan(buttons, 2);
        body.RowCount++;

        _status.Text = "Vul de NAS-gegevens in en kies een verbinding.";
        _status.AutoSize = true; _status.Dock = DockStyle.Fill;
        _status.MaximumSize = new Size(680, 0);
        _status.ForeColor = Color.FromArgb(61, 79, 99); _status.Padding = new Padding(0, 18, 0, 5);
        body.Controls.Add(_status, 0, body.RowCount);
        body.SetColumnSpan(_status, 2);
        body.RowCount++;

        _lanConnect.Click += async (_, _) => await Execute(async () =>
        {
            SaveSettings();
            await Mount(NativeSmb.ValidateLanShare(_lan.Text));
        });
        _remoteConnect.Click += async (_, _) => await Execute(async () =>
        {
            SaveSettings();
            using var key = Password(_authKey);
            if (!await _tailnet.IsOnlineAsync() && key.Length > 0)
                await _tailnet.RegisterAsync(key);
            var status = await _tailnet.StatusAsync();
            var share = NativeSmb.ResolveTailnetShare(_host.Text, status);
            await Mount(share);
        });
        _disconnect.Click += async (_, _) => await Execute(() =>
        {
            NativeSmb.Disconnect(_drive.Text);
            SetStatus("Schijf ontkoppeld. Bestaande NAS-bestanden zijn niet gewijzigd.");
            return Task.CompletedTask;
        });
        _disableTailnet.Click += async (_, _) => await Execute(async () =>
        {
            if (MessageBox.Show("Sluit eerst alle bestanden op de Tailscale-schijf. De privé-Tailscale-verbinding stoppen?",
                    "Tailscale stoppen", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            await _tailnet.DisconnectAsync();
            SetStatus("Privé-Tailscale-verbinding gestopt. De NAS- en andere VPN-configuraties blijven ongewijzigd.");
        });

        LoadSettings();
    }

    private async Task Mount(string root)
    {
        using var password = Password(_nasPassword);
        await Task.Run(() => NativeSmb.Connect(_drive.Text, root, _nasUser.Text, password));
        SetStatus($"Schijf {_drive.Text} gekoppeld met native Windows SMB: {root}. Geen Dokany.");
        try { Process.Start(new ProcessStartInfo("explorer.exe", _drive.Text + "\\") { UseShellExecute = true }); }
        catch { /* Connection succeeded even when Explorer launch is disabled. */ }
    }

    private static SecureString Password(TextBox input)
    {
        var result = new SecureString();
        foreach (var character in input.Text) result.AppendChar(character);
        result.MakeReadOnly();
        input.Clear();
        return result;
    }

    private async Task Execute(Func<Task> task)
    {
        if (_busy) return;
        _busy = true;
        foreach (var button in new[] { _lanConnect, _remoteConnect, _disconnect, _disableTailnet }) button.Enabled = false;
        try { await task(); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally
        {
            _busy = false;
            foreach (var button in new[] { _lanConnect, _remoteConnect, _disconnect, _disableTailnet }) button.Enabled = true;
        }
    }

    private void SetStatus(string text, bool error = false)
    {
        _status.Text = text;
        _status.ForeColor = error ? Color.FromArgb(183, 42, 42) : Color.FromArgb(39, 92, 65);
    }

    private static void Row(TableLayoutPanel layout, string label, Control control)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowCount++;
        layout.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = true, Margin = new Padding(0, 12, 0, 12) }, 0, layout.RowCount - 1);
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 7, 0, 7);
        layout.Controls.Add(control, 1, layout.RowCount - 1);
    }

    private sealed record SavedSettings(string Lan, string Host, string NasUser, string Drive);

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var settings = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(_settingsPath));
            if (settings is null) return;
            _lan.Text = settings.Lan;
            _host.Text = settings.Host;
            _nasUser.Text = settings.NasUser;
            if (_drive.Items.Contains(settings.Drive)) _drive.SelectedItem = settings.Drive;
        }
        catch { SetStatus("Instellingen konden niet worden geladen; vul je NAS-gegevens opnieuw in.", true); }
    }

    private void SaveSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var settings = new SavedSettings(_lan.Text.Trim(), _host.Text.Trim(), _nasUser.Text.Trim(), _drive.Text);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings));
        // Deliberately never persist the Tailscale key or SMB password here.
    }
}
