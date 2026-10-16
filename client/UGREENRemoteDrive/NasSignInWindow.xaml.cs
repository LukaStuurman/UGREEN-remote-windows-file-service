using System.Windows;

namespace UGREENRemoteDrive;

public partial class NasSignInWindow : Window
{
    private readonly string _root;
    private readonly SmbSignInService _signIn;
    private bool _busy;
    internal string? SaveWarning { get; private set; }

    internal NasSignInWindow(string root, SmbSignInService signIn)
    {
        InitializeComponent();
        _root = SmbSignInService.ValidateRoot(root);
        _signIn = signIn;
        ShareText.Text = _root;
        RememberBox.IsChecked = true;
        Loaded += (_, _) => UserNameBox.Focus();
        Closing += (_, e) => { if (_busy) e.Cancel = true; else SecretBox.Clear(); };
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var userName = UserNameBox.Text;
        var remember = RememberBox.IsChecked == true;
        using var password = SecretBox.SecurePassword;
        SecretBox.Clear();
        _busy = true;
        SignInButton.IsEnabled = CancelButton.IsEnabled = UserNameBox.IsEnabled =
            SecretBox.IsEnabled = RememberBox.IsEnabled = false;
        FeedbackText.Text = "Aanmelden bij de NAS…";
        try
        {
            var result = await Task.Run(() => _signIn.SignIn(_root, userName, password, remember));
            if (result.Success)
            {
                SaveWarning = result.Warning;
                _busy = false;
                DialogResult = true;
            }
            else FeedbackText.Text = result.Message;
        }
        catch (ArgumentException ex) { FeedbackText.Text = ex.Message; }
        catch { FeedbackText.Text = "Aanmelden kon niet worden afgerond. Controleer de verbinding en probeer opnieuw."; }
        finally
        {
            _busy = false;
            SignInButton.IsEnabled = CancelButton.IsEnabled = UserNameBox.IsEnabled =
                SecretBox.IsEnabled = RememberBox.IsEnabled = true;
            SecretBox.Focus();
        }
    }
}
