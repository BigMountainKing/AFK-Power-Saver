using System.Runtime.Versioning;
using System.Security.Principal;
using System.Windows;
using System.Windows.Interop;

namespace EcoPause.Desktop;

[SupportedOSPlatform("windows")]
public partial class App : Application, IDisposable
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show(
                "AFK Power Saver must be started as a normal user. Privileged hardware work is isolated in the UAC helper.",
                "AFK Power Saver safety stop",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(3);
            return;
        }

        var isolatedSmokeTest =
            IsSmokeTestEnabled("ACTIVITY") ||
            IsSmokeTestEnabled("HOTKEY") ||
            IsSmokeTestEnabled("UI") ||
            IsSmokeTestEnabled("CLOSE") ||
            IsSmokeTestEnabled("TRAY");
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            isolatedSmokeTest
                ? @"Local\AFKPowerSaver-Desktop-SingleInstance-Smoke"
                : @"Local\AFKPowerSaver-Desktop-SingleInstance",
            out var isFirstInstance);
        if (!isFirstInstance)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            MessageBox.Show(
                "AFK Power Saver is already running. Use the existing window or its assigned hotkey.",
                "AFK Power Saver already running",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(4);
            return;
        }

        if (string.Equals(
            Environment.GetEnvironmentVariable("AFKPOWERSAVER_ACTIVITY_SMOKE_TEST"),
            "1",
            StringComparison.Ordinal) || IsEnabled("ECOPAUSE_ACTIVITY_SMOKE_TEST"))
        {
            var snapshot = WindowsActivityObserver.ReadCurrent();
            Shutdown(snapshot.Available ? 0 : 5);
            return;
        }

        MainWindow = new MainWindow();
        if (string.Equals(
            Environment.GetEnvironmentVariable("AFKPOWERSAVER_HOTKEY_SMOKE_TEST"),
            "1",
            StringComparison.Ordinal) || IsEnabled("ECOPAUSE_HOTKEY_SMOKE_TEST"))
        {
            var window = (MainWindow)MainWindow;
            var windowHandle = new WindowInteropHelper(window).EnsureHandle();
            using var registration = new GlobalHotKeyRegistration(windowHandle, HotKeyDefinition.Default);
            window.Dispose();
            Shutdown(0);
            return;
        }

        if (string.Equals(
            Environment.GetEnvironmentVariable("AFKPOWERSAVER_UI_SMOKE_TEST"),
            "1",
            StringComparison.Ordinal) || IsEnabled("ECOPAUSE_UI_SMOKE_TEST"))
        {
            const int previewWidth = 1420;
            const int previewHeight = 970;
            MainWindow.Measure(new Size(previewWidth, previewHeight));
            MainWindow.Arrange(new Rect(0, 0, previewWidth, previewHeight));
            MainWindow.UpdateLayout();
            ((MainWindow)MainWindow).Dispose();
            Shutdown(0);
            return;
        }

        if (string.Equals(
            Environment.GetEnvironmentVariable("AFKPOWERSAVER_CLOSE_SMOKE_TEST"),
            "1",
            StringComparison.Ordinal) || IsEnabled("ECOPAUSE_CLOSE_SMOKE_TEST"))
        {
            MainWindow.Close();
            return;
        }

        if (IsSmokeTestEnabled("TRAY"))
        {
            var window = (MainWindow)MainWindow;
            window.RunTraySmokeTest();
            var dialog = new ClosePreferenceDialog();
            dialog.Measure(new Size(520, 275));
            dialog.Arrange(new Rect(0, 0, 520, 275));
            dialog.UpdateLayout();
            dialog.Close();
            window.Dispose();
            Shutdown(0);
            return;
        }

        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        if (_singleInstanceMutex is not null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        GC.SuppressFinalize(this);
    }

    private static bool IsEnabled(string variable) =>
        string.Equals(
            Environment.GetEnvironmentVariable(variable),
            "1",
            StringComparison.Ordinal);

    private static bool IsSmokeTestEnabled(string name) =>
        IsEnabled($"AFKPOWERSAVER_{name}_SMOKE_TEST") ||
        IsEnabled($"ECOPAUSE_{name}_SMOKE_TEST");
}
