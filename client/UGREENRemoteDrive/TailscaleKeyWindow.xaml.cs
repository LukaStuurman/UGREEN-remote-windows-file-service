using System.Windows;

namespace UGREENRemoteDrive;

public partial class TailscaleKeyWindow : Window
{
    private readonly EmbeddedTailscaleBackend _backend;
    private bool _busy;
    private bool _waitingForApproval;
    internal TailscaleKeyWindow(EmbeddedTailscaleBackend backend, bool waitingForApproval = false)
    {
        InitializeComponent(); _backend = backend;
        if (waitingForApproval) ShowApprovalRequired();
        Loaded += (_, _) => { if (_waitingForApproval) CheckApprovalButton.Focus(); else KeyBox.Focus(); };
        Closing += (_, e) => { if (_busy) e.Cancel = true; else KeyBox.Clear(); };
    }
    private async void Authorize_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        using var key = KeyBox.SecurePassword; KeyBox.Clear();
        if (key.Length < 30) { FeedbackText.Text = "Vul een geldige Tailscale auth key in."; return; }
        _busy = true;
        SetBusy(true);
        FeedbackText.Text = "Deze app registreren bij Tailscale…";
        try
        {
            await Task.Run(() => _backend.Authorize(key));
            ShowStatus(await Task.Run(_backend.GetStatus));
        }
        catch (DriveApiException ex) { FeedbackText.Text = ex.Message; }
        catch (DriveDisconnectedException ex) { FeedbackText.Text = ex.Message; }
        catch { FeedbackText.Text = "De Tailscale-registratie kon niet worden afgerond. Controleer je internetverbinding en probeer opnieuw."; }
        finally
        {
            SetBusy(false);
        }
    }

    private async void CheckApproval_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try { ShowStatus(await Task.Run(_backend.GetStatus)); }
        catch { FeedbackText.Text = "De goedkeuring kon niet worden gecontroleerd. Controleer je internetverbinding en probeer opnieuw."; }
        finally { SetBusy(false); }
    }

    private void ShowStatus(TailnetStatus status)
    {
        if (status.State == "Running") { _busy = false; DialogResult = true; return; }
        if (status.State == "NeedsMachineAuth") { ShowApprovalRequired(); return; }
        _waitingForApproval = false;
        CheckApprovalButton.Visibility = Visibility.Collapsed;
        CheckApprovalButton.IsDefault = false;
        AuthorizeButton.Visibility = Visibility.Visible;
        AuthorizeButton.IsDefault = true;
        FeedbackText.Text = "De registratie is nog niet afgerond. Dit is geen bevestiging dat apparaatgoedkeuring nodig is. Controleer je internetverbinding en probeer opnieuw.";
    }

    private void ShowApprovalRequired()
    {
        _waitingForApproval = true;
        KeyBox.Clear();
        KeyBox.IsEnabled = false;
        AuthorizeButton.Visibility = Visibility.Collapsed;
        AuthorizeButton.IsDefault = false;
        CheckApprovalButton.Visibility = Visibility.Visible;
        CheckApprovalButton.IsDefault = true;
        FeedbackText.Text = "De app is geregistreerd en wacht op apparaatgoedkeuring. Open Tailscale → Machines, keur ugreen-remote-drive goed en klik daarna op Goedkeuring controleren. Je hoeft de sleutel niet opnieuw in te voeren.";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        AuthorizeButton.IsEnabled = CancelButton.IsEnabled = CheckApprovalButton.IsEnabled = !busy;
        KeyBox.IsEnabled = !busy && !_waitingForApproval;
    }
}
