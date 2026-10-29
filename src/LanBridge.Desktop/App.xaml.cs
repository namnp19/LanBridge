using System.Windows;

namespace LanBridge.Desktop;

public partial class App : Application
{
    private Mutex? _singleInstance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, @"Local\LanBridge-WinDesktop", out var acquired);
        if (!acquired)
        {
            MessageBox.Show("LanBridge đang chạy trong System Tray.", "LanBridge");
            Shutdown();
            return;
        }
        try
        {
            var engine = new LanBridgeEngine();
            var window = new MainWindow(engine);
            MainWindow = window;
            window.Show();
            await engine.StartAsync();
            if (e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase)) window.Hide();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Không khởi động được LanBridge:\n" + ex.Message,
                "LanBridge", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstance != null)
        {
            try { _singleInstance.ReleaseMutex(); } catch (ApplicationException) { }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }
}
