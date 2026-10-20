using System.Windows;

namespace UGREENRemoteDrive;

public partial class TailscaleKeyWindow : Window
{
    private readonly Func<System.Security.SecureString, Task> _authorize;
    private readonly Func<Task<TailnetStatus>> _getStatus;
    private bool _busy;
    private bool _waitingForApproval;
    internal TailscaleKeyWindow(EmbeddedTailscaleBackend backend, bool waitingForApproval = false)
        : this(key => Task.Run(() => backend.Authorize(key)), () => Task.Run(backend.GetStatus), waitingForApproval) { }

    internal TailscaleKeyWindow(WindowsSmbTailnet backend, bool waitingForApproval = false)
        : this(backend.AuthorizeAsync, backend.GetStatusAsync, waitingForApproval)
    {
        StorageNote.Text = "Deze Windows-SMB-route gebruikt een eigen Tailscale-identiteit, apart van de gewone ingebouwde verbinding. De auth key wordt niet in instellingen of logs opgeslagen. Apparaatgoedkeuring en toegangsregels blijven gelden.";
    }

    private TailscaleKeyWindow(Func<System.Security.SecureString, Task> authorize,
        Func<Task<TailnetStatus>> getStatus, bool waitingForApproval)
    {
        InitializeComponent(); _authorize = authorize; _getStatus = getStatus;
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
            await _authorize(key);
            ShowStatus(await _getStatus());
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
        try { ShowStatus(await _getStatus()); }
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
        FeedbackText.Text = "De app is geregistreerd en wacht op apparaatgoedkeuring. Open Tailscale → Machines, keur het nieuwe Remote Drive-apparaat goed en klik daarna op Goedkeuring controleren. Je hoeft de sleutel niet opnieuw in te voeren.";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        AuthorizeButton.IsEnabled = CancelButton.IsEnabled = CheckApprovalButton.IsEnabled = !busy;
        KeyBox.IsEnabled = !busy && !_waitingForApproval;
    }
}
