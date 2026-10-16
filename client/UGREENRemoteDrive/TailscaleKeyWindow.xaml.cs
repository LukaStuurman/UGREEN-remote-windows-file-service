using System.Windows;

namespace UGREENRemoteDrive;

public partial class TailscaleKeyWindow : Window
{
    private readonly EmbeddedTailscaleBackend _backend;
    private bool _busy;
    internal TailscaleKeyWindow(EmbeddedTailscaleBackend backend)
    {
        InitializeComponent(); _backend = backend;
        Loaded += (_, _) => KeyBox.Focus();
        Closing += (_, e) => { if (_busy) e.Cancel = true; else KeyBox.Clear(); };
    }
    private async void Authorize_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        using var key = KeyBox.SecurePassword; KeyBox.Clear();
        if (key.Length < 30) { FeedbackText.Text = "Vul een geldige Tailscale auth key in."; return; }
        _busy = true;
        AuthorizeButton.IsEnabled = CancelButton.IsEnabled = KeyBox.IsEnabled = false;
        FeedbackText.Text = "Toestemming controleren…";
        try
        {
            await Task.Run(() => _backend.Authorize(key));
            for (var attempt = 0; attempt < 45; attempt++)
            {
                var status = await Task.Run(_backend.GetStatus);
                if (status.State == "Running") { _busy = false; DialogResult = true; return; }
                if (status.State == "NeedsMachineAuth")
                {
                    FeedbackText.Text = "Deze app wacht op goedkeuring door de Tailscale-beheerder. Geef het apparaat ugreen-remote-drive toestemming en klik daarna op Verbinden in de app.";
                    return;
                }
                await Task.Delay(500);
            }
            FeedbackText.Text = "De verbinding is nog niet goedgekeurd. Controleer de sleutel en de apparaatgoedkeuring in Tailscale.";
        }
        catch { FeedbackText.Text = "Deze sleutel kon de verbinding niet toestaan. Controleer of hij geldig is en voor het juiste Tailscale-netwerk is gemaakt."; }
        finally
        {
            _busy = false;
            AuthorizeButton.IsEnabled = CancelButton.IsEnabled = KeyBox.IsEnabled = true;
        }
    }
}
