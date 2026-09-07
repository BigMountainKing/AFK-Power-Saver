using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AFKPowerSaver.OverlayModel;
using AFKPowerSaver.Overlay.Windows;

namespace AFKPowerSaver.OverlayCanary;

public partial class MainWindow : Window, IDisposable
{
    private static readonly System.Windows.Media.Brush ReadyBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(103, 232, 165));
    private static readonly System.Windows.Media.Brush MutedBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(158, 176, 194));
    private static readonly System.Windows.Media.Brush ErrorBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113));

    private readonly DispatcherTimer _countdownTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(200)
    };
    private EmergencyHotKeyRegistration? _emergencyHotKey;
    private ScreenDimmingSession? _overlaySession;
    private DateTimeOffset _overlayDeadline;
    private bool _disposed;

    public MainWindow()
    {
        InitializeComponent();
        _countdownTimer.Tick += CountdownTimer_Tick;
        UpdateOpacityText();
        MonitorStatusText.Text = $"Displays detected: {DisplayTargetCatalog.GetOptions().Count - 1}";
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            _emergencyHotKey = new EmergencyHotKeyRegistration(handle);
            _emergencyHotKey.Pressed += EmergencyHotKey_Pressed;
            RunStatusText.Text = "Ready · Ctrl+Shift+F12 emergency exit registered";
            RunStatusText.Foreground = ReadyBrush;
            RunOverlayButton.IsEnabled = true;
        }
        catch (Win32Exception exception)
        {
            RunStatusText.Text = $"Blocked · emergency hotkey unavailable: {exception.Message}";
            RunStatusText.Foreground = ErrorBrush;
            RunOverlayButton.IsEnabled = false;
        }
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        UpdateOpacityText();

    private void RunOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlaySession is not null)
        {
            EndOverlay("Overlay dismissed from the test window.");
            return;
        }

        try
        {
            var settings = OverlayCanaryPolicy.Create((int)Math.Round(OpacitySlider.Value));
            _overlaySession = ScreenDimmingSession.Show(
                settings.OpacityPercent,
                DisplayTargetPolicy.AllDisplaysTargetId);
            _overlayDeadline = DateTimeOffset.UtcNow + settings.HoldDuration;
            OpacitySlider.IsEnabled = false;
            RunOverlayButton.Content = "Dismiss overlay now";
            RunStatusText.Foreground = ReadyBrush;
            _countdownTimer.Start();
            UpdateCountdownStatus();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentOutOfRangeException or Win32Exception)
        {
            _overlaySession?.Dispose();
            _overlaySession = null;
            RunStatusText.Text = $"Overlay test stopped safely · {exception.Message}";
            RunStatusText.Foreground = ErrorBrush;
            OpacitySlider.IsEnabled = true;
            RunOverlayButton.Content = "Run 15-second overlay test";
        }
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            _overlaySession?.ReassertTopmost();
        }
        catch (Win32Exception exception)
        {
            EndOverlay($"Overlay stopped safely · {exception.Message}");
            RunStatusText.Foreground = ErrorBrush;
            return;
        }

        if (DateTimeOffset.UtcNow >= _overlayDeadline)
        {
            EndOverlay("Automatic 15-second dismissal: PASS");
            return;
        }

        UpdateCountdownStatus();
    }

    private void EmergencyHotKey_Pressed(object? sender, EventArgs e)
    {
        if (_overlaySession is not null)
        {
            EndOverlay("Ctrl+Shift+F12 emergency dismissal: PASS");
        }
    }

    private void EndOverlay(string status)
    {
        _countdownTimer.Stop();
        _overlaySession?.Dispose();
        _overlaySession = null;
        OpacitySlider.IsEnabled = true;
        RunOverlayButton.Content = "Run 15-second overlay test";
        RunStatusText.Text = status;
        RunStatusText.Foreground = ReadyBrush;
    }

    private void UpdateCountdownStatus()
    {
        var remaining = _overlayDeadline - DateTimeOffset.UtcNow;
        var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        RunStatusText.Text = $"Overlay active · click-through enabled · auto-dismiss in {seconds}s";
        RunStatusText.Foreground = MutedBrush;
    }

    private void UpdateOpacityText()
    {
        if (OpacityValueText is not null)
        {
            OpacityValueText.Text = $"{Math.Round(OpacitySlider.Value):0}%";
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _countdownTimer.Stop();
        _countdownTimer.Tick -= CountdownTimer_Tick;
        _overlaySession?.Dispose();
        _overlaySession = null;
        if (_emergencyHotKey is not null)
        {
            _emergencyHotKey.Pressed -= EmergencyHotKey_Pressed;
            _emergencyHotKey.Dispose();
            _emergencyHotKey = null;
        }

        GC.SuppressFinalize(this);
    }
}
