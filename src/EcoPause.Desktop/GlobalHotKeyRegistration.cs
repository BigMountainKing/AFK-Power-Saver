using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Interop;

namespace EcoPause.Desktop;

[SupportedOSPlatform("windows")]
internal sealed partial class GlobalHotKeyRegistration : IDisposable
{
    private const int ActivationHotKeyId = 0x4550;
    private const int HotKeyMessage = 0x0312;
    private const uint ModifierNoRepeat = 0x4000;

    private readonly nint _windowHandle;
    private readonly HwndSource _source;
    private bool _disposed;

    public GlobalHotKeyRegistration(nint windowHandle, HotKeyDefinition definition)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));
        }

        _windowHandle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle)
            ?? throw new InvalidOperationException("The AFK Power Saver window message source is unavailable.");
        _source.AddHook(WindowMessageHook);

        if (!RegisterHotKey(
                windowHandle,
                ActivationHotKeyId,
                (uint)definition.Modifiers | ModifierNoRepeat,
                definition.VirtualKey))
        {
            _source.RemoveHook(WindowMessageHook);
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"{definition.DisplayName} could not be registered as the AFK Power Saver hotkey.");
        }

        Definition = definition;
    }

    public HotKeyDefinition Definition { get; }

    public event EventHandler? Pressed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = UnregisterHotKey(_windowHandle, ActivationHotKeyId);
        _source.RemoveHook(WindowMessageHook);
        GC.SuppressFinalize(this);
    }

    private nint WindowMessageHook(
        nint windowHandle,
        int message,
        nint wordParameter,
        nint longParameter,
        ref bool handled)
    {
        if (message == HotKeyMessage && wordParameter == ActivationHotKeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return 0;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(
        nint windowHandle,
        int identifier,
        uint modifiers,
        uint virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint windowHandle, int identifier);
}
