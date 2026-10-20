namespace UGREENRemoteDrive;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        if (e.Args.Length == 1 && e.Args[0] == "--verify-windows-smb-components")
        {
            // Offline packaging check: no MainWindow, settings, enrollment, or driver startup.
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            Shutdown(Task.Run(Native.ManagedTailnet.SelfTest).GetAwaiter().GetResult());
            return;
        }
        StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
        base.OnStartup(e);
    }
}
