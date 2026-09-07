using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AFKPowerSaver.OverlayCanary;

internal sealed partial class EmergencyHotKeyRegistration : IDisposable
{
    private const int HotKeyId = 0x4146;
    private const int HotKeyMessage = 0x0312;
    private const uint ControlModifier = 0x0002;
    private const uint ShiftModifier = 0x0004;
    private const uint NoRepeatModifier = 0x4000;
    private const uint F12VirtualKey = 0x7B;

    private readonly nint _windowHandle;
    private readonly HwndSource _source;
    private bool _disposed;

    public EmergencyHotKeyRegistration(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));
        }

        _windowHandle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle)
            ?? throw new InvalidOperationException("The overlay test window message source is unavailable.");
        _source.AddHook(WindowMessageHook);

        if (!RegisterHotKey(
                windowHandle,
                HotKeyId,
                ControlModifier | ShiftModifier | NoRepeatModifier,
                F12VirtualKey))
        {
            _source.RemoveHook(WindowMessageHook);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Ctrl+Shift+F12 is already in use.");
        }
    }

    public event EventHandler? Pressed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = UnregisterHotKey(_windowHandle, HotKeyId);
        _source.RemoveHook(WindowMessageHook);
    }

    private nint WindowMessageHook(
        nint windowHandle,
        int message,
        nint wordParameter,
        nint longParameter,
        ref bool handled)
    {
        if (message == HotKeyMessage && wordParameter == HotKeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return 0;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint windowHandle, int identifier, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint windowHandle, int identifier);
}
