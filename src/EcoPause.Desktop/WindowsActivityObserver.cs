using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Threading;
using Microsoft.Win32;

namespace EcoPause.Desktop;

internal sealed record WindowsActivitySnapshot(
    bool Available,
    TimeSpan IdleDuration,
    bool SessionLocked,
    string Message);

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsActivityObserver : IDisposable
{
    private readonly DispatcherTimer _timer;
    private bool _sessionLocked;
    private bool _started;
    private bool _disposed;

    public WindowsActivityObserver()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_Tick;
    }

    public event EventHandler<WindowsActivitySnapshot>? SnapshotChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;
        _timer.Start();
        PublishSnapshot();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        if (_started)
        {
            SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
        }
    }

    private void Timer_Tick(object? sender, EventArgs e) => PublishSnapshot();

    private void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            _sessionLocked = true;
        }
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            _sessionLocked = false;
        }

        PublishSnapshot();
    }

    private void PublishSnapshot()
    {
        SnapshotChanged?.Invoke(this, ReadCurrent(_sessionLocked));
    }

    internal static WindowsActivitySnapshot ReadCurrent(bool sessionLocked = false)
    {
        try
        {
            return new WindowsActivitySnapshot(
                Available: true,
                ReadIdleDuration(),
                sessionLocked,
                "Windows activity signals ready.");
        }
        catch (Win32Exception)
        {
            return new WindowsActivitySnapshot(
                Available: false,
                TimeSpan.Zero,
                sessionLocked,
                "Windows activity signals are unavailable.");
        }
    }

    private static TimeSpan ReadIdleDuration()
    {
        var input = new LastInputInfo
        {
            Size = (uint)Marshal.SizeOf<LastInputInfo>()
        };

        if (!GetLastInputInfo(ref input))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var currentTick = unchecked((uint)Environment.TickCount64);
        var elapsedMilliseconds = unchecked(currentTick - input.TickCount);
        return TimeSpan.FromMilliseconds(elapsedMilliseconds);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo input);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint TickCount;
    }
}
