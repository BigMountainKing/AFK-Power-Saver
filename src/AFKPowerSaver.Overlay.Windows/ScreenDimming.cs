using System.ComponentModel;
using System.Runtime.InteropServices;
using AFKPowerSaver.OverlayModel;
using DrawingRectangle = System.Drawing.Rectangle;

namespace AFKPowerSaver.Overlay.Windows;

public sealed record DisplayTargetOption(string Id, string DisplayName, bool IsAllDisplays)
{
    public override string ToString() => DisplayName;
}

public static class DisplayTargetCatalog
{
    public static IReadOnlyList<DisplayTargetOption> GetOptions()
    {
        var screens = GetOrderedScreens();
        var options = new List<DisplayTargetOption>(screens.Length + 1)
        {
            new(DisplayTargetPolicy.AllDisplaysTargetId, $"All monitors ({screens.Length})", true)
        };

        for (var index = 0; index < screens.Length; index++)
        {
            var screen = screens[index];
            var primary = screen.Primary ? " · Primary" : string.Empty;
            options.Add(new DisplayTargetOption(
                screen.DeviceName,
                $"Monitor {index + 1}{primary} · {screen.Bounds.Width}×{screen.Bounds.Height}",
                false));
        }

        return options;
    }

    public static string NormalizeTargetId(string? targetId)
    {
        var screens = GetOrderedScreens();
        return DisplayTargetPolicy.NormalizeSavedTarget(targetId, screens.Select(screen => screen.DeviceName));
    }

    public static string[] NormalizeTargetIds(IEnumerable<string>? targetIds)
    {
        var screens = GetOrderedScreens();
        return DisplayTargetPolicy.NormalizeSavedTargets(
            targetIds,
            screens.Select(screen => screen.DeviceName));
    }

    internal static IReadOnlyList<System.Windows.Forms.Screen> ResolveScreens(string? targetId, out string resolvedTargetId)
    {
        var screens = GetOrderedScreens();
        if (screens.Length == 0)
        {
            throw new InvalidOperationException("Windows did not report any active displays.");
        }

        resolvedTargetId = DisplayTargetPolicy.NormalizeSavedTarget(
            targetId,
            screens.Select(screen => screen.DeviceName));
        if (string.Equals(resolvedTargetId, DisplayTargetPolicy.AllDisplaysTargetId, StringComparison.Ordinal))
        {
            return screens;
        }

        var selectedTargetId = resolvedTargetId;
        return
        [
            screens.Single(screen => string.Equals(
                screen.DeviceName,
                selectedTargetId,
                StringComparison.OrdinalIgnoreCase))
        ];
    }

    internal static IReadOnlyList<System.Windows.Forms.Screen> ResolveScreens(
        IEnumerable<string>? targetIds,
        out string[] resolvedTargetIds)
    {
        var screens = GetOrderedScreens();
        if (screens.Length == 0)
        {
            throw new InvalidOperationException("Windows did not report any active displays.");
        }

        resolvedTargetIds = DisplayTargetPolicy.NormalizeSavedTargets(
            targetIds,
            screens.Select(screen => screen.DeviceName));
        if (resolvedTargetIds.Length == 0)
        {
            throw new InvalidOperationException("Select at least one connected display to dim.");
        }
        if (resolvedTargetIds.Length == 1 &&
            string.Equals(resolvedTargetIds[0], DisplayTargetPolicy.AllDisplaysTargetId, StringComparison.Ordinal))
        {
            return screens;
        }

        var selected = resolvedTargetIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return screens.Where(screen => selected.Contains(screen.DeviceName)).ToArray();
    }

    private static System.Windows.Forms.Screen[] GetOrderedScreens() =>
        System.Windows.Forms.Screen.AllScreens
            .OrderByDescending(screen => screen.Primary)
            .ThenBy(screen => screen.Bounds.Left)
            .ThenBy(screen => screen.Bounds.Top)
            .ThenBy(screen => screen.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

public sealed class ScreenDimmingSession : IDisposable
{
    private readonly IReadOnlyList<NativeClickThroughOverlayWindow> _windows;
    private bool _disposed;

    private ScreenDimmingSession(
        IReadOnlyList<NativeClickThroughOverlayWindow> windows,
        IReadOnlyList<string> resolvedTargetIds,
        int opacityPercent)
    {
        _windows = windows;
        ResolvedTargetIds = resolvedTargetIds;
        OpacityPercent = opacityPercent;
    }

    public IReadOnlyList<string> ResolvedTargetIds { get; }

    public string ResolvedTargetId => ResolvedTargetIds.Count == 1
        ? ResolvedTargetIds[0]
        : DisplayTargetPolicy.AllDisplaysTargetId;

    public int OpacityPercent { get; }

    public int DisplayCount => _windows.Count;

    public static ScreenDimmingSession Show(int opacityPercent, string? targetId)
    {
        var settings = OverlayCanaryPolicy.Create(opacityPercent);
        var screens = DisplayTargetCatalog.ResolveScreens(targetId, out var resolvedTargetId);
        var windows = new List<NativeClickThroughOverlayWindow>(screens.Count);
        try
        {
            foreach (var screen in screens)
            {
                windows.Add(NativeClickThroughOverlayWindow.Create(screen.Bounds, settings.OpacityFraction));
            }

            return new ScreenDimmingSession(windows, [resolvedTargetId], opacityPercent);
        }
        catch
        {
            foreach (var window in windows)
            {
                window.Dispose();
            }

            throw;
        }
    }

    public static ScreenDimmingSession Show(int opacityPercent, IReadOnlyCollection<string> targetIds)
    {
        ArgumentNullException.ThrowIfNull(targetIds);
        var settings = OverlayCanaryPolicy.Create(opacityPercent);
        var screens = DisplayTargetCatalog.ResolveScreens(targetIds, out var resolvedTargetIds);
        var windows = new List<NativeClickThroughOverlayWindow>(screens.Count);
        try
        {
            foreach (var screen in screens)
            {
                windows.Add(NativeClickThroughOverlayWindow.Create(screen.Bounds, settings.OpacityFraction));
            }

            return new ScreenDimmingSession(windows, resolvedTargetIds, opacityPercent);
        }
        catch
        {
            foreach (var window in windows)
            {
                window.Dispose();
            }

            throw;
        }
    }

    public void ReassertTopmost()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var window in _windows)
        {
            window.ReassertTopmost();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var window in _windows)
        {
            window.Dispose();
        }
    }
}

internal sealed partial class NativeClickThroughOverlayWindow : IDisposable
{
    private const int ExtendedWindowStyleIndex = -20;
    private const uint TopmostStyle = 0x00000008;
    private const uint TransparentStyle = 0x00000020;
    private const uint ToolWindowStyle = 0x00000080;
    private const uint LayeredStyle = 0x00080000;
    private const uint NoActivateStyle = 0x08000000;
    private const uint PopupStyle = 0x80000000;
    private const uint AlphaAttributeFlag = 0x00000002;
    private const uint NoSizePositionFlag = 0x0001;
    private const uint NoMovePositionFlag = 0x0002;
    private const uint NoActivatePositionFlag = 0x0010;
    private const uint ShowWindowPositionFlag = 0x0040;
    private const uint RedrawInvalidateFlag = 0x0001;
    private const uint RedrawEraseFlag = 0x0004;
    private const uint RedrawUpdateNowFlag = 0x0100;
    private const uint NonClientHitTestMessage = 0x0084;
    private const uint MouseActivateMessage = 0x0021;
    private const int TransparentHitTestResult = -1;
    private const int DoNotActivateResult = 3;
    private const int BlackBrushStockObject = 4;
    private const string WindowTitle = "AFK Power Saver dimming overlay";
    private static readonly nint TopmostWindow = new(-1);
    private static readonly object ClassRegistrationLock = new();
    private static WindowProcedure? _windowProcedure;
    private static nint _classNamePointer;
    private static nint _moduleHandle;
    private static ushort _windowClassAtom;

    private readonly byte _opacityAlpha;
    private nint _windowHandle;
    private bool _disposed;

    private NativeClickThroughOverlayWindow(nint windowHandle, byte opacityAlpha)
    {
        _windowHandle = windowHandle;
        _opacityAlpha = opacityAlpha;
    }

    public static NativeClickThroughOverlayWindow Create(DrawingRectangle bounds, double opacity)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "Display bounds must be positive.");
        }
        if (opacity is < 0.1 or > 0.95)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity));
        }

        EnsureWindowClass();
        var opacityAlpha = checked((byte)Math.Round(opacity * byte.MaxValue, MidpointRounding.AwayFromZero));
        var safetyStyles = TopmostStyle | TransparentStyle | ToolWindowStyle | LayeredStyle | NoActivateStyle;
        var handle = CreateWindowEx(
            safetyStyles,
            _classNamePointer,
            WindowTitle,
            PopupStyle,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            0,
            0,
            _moduleHandle,
            0);
        if (handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not create the native dimming overlay.");
        }

        var window = new NativeClickThroughOverlayWindow(handle, opacityAlpha);
        try
        {
            window.VerifySafetyStyles(safetyStyles);
            window.ApplyAndVerifyOpacity();
            if (!SetWindowPos(
                    handle,
                    TopmostWindow,
                    bounds.Left,
                    bounds.Top,
                    bounds.Width,
                    bounds.Height,
                    NoActivatePositionFlag | ShowWindowPositionFlag))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The overlay could not cover a detected display.");
            }

            if (!RedrawWindow(
                    handle,
                    0,
                    0,
                    RedrawInvalidateFlag | RedrawEraseFlag | RedrawUpdateNowFlag))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The black overlay surface could not be drawn.");
            }

            return window;
        }
        catch
        {
            window.Dispose();
            throw;
        }
    }

    public void ReassertTopmost()
    {
        if (_disposed || _windowHandle == 0)
        {
            return;
        }

        if (!SetWindowPos(
                _windowHandle,
                TopmostWindow,
                0,
                0,
                0,
                0,
                NoSizePositionFlag | NoMovePositionFlag | NoActivatePositionFlag))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The overlay could not remain above the desktop.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_windowHandle != 0)
        {
            _ = DestroyWindow(_windowHandle);
            _windowHandle = 0;
        }
    }

    private static void EnsureWindowClass()
    {
        if (_windowClassAtom != 0)
        {
            return;
        }

        lock (ClassRegistrationLock)
        {
            if (_windowClassAtom != 0)
            {
                return;
            }

            _windowProcedure = WindowMessageProcedure;
            _classNamePointer = Marshal.StringToHGlobalUni($"AFKPowerSaverOverlay-{Environment.ProcessId}");
            _moduleHandle = GetModuleHandle(0);
            var windowClass = new WindowClass
            {
                Size = checked((uint)Marshal.SizeOf<WindowClass>()),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
                Instance = _moduleHandle,
                BackgroundBrush = GetStockObject(BlackBrushStockObject),
                ClassName = _classNamePointer
            };
            _windowClassAtom = RegisterClassEx(ref windowClass);
            if (_windowClassAtom == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The native overlay window class could not be registered.");
            }
        }
    }

    private void VerifySafetyStyles(uint expectedStyles)
    {
        var appliedStyles = unchecked((uint)GetWindowLongPtr(_windowHandle, ExtendedWindowStyleIndex).ToInt64());
        if ((appliedStyles & expectedStyles) != expectedStyles)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The overlay click-through safety styles were not applied.");
        }
    }

    private void ApplyAndVerifyOpacity()
    {
        if (!SetLayeredWindowAttributes(_windowHandle, 0, _opacityAlpha, AlphaAttributeFlag))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows did not accept the selected overlay opacity.");
        }

        if (!GetLayeredWindowAttributes(_windowHandle, out _, out var appliedAlpha, out var appliedFlags) ||
            (appliedFlags & AlphaAttributeFlag) == 0 ||
            appliedAlpha != _opacityAlpha)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The selected overlay opacity did not read back exactly.");
        }
    }

    private static nint WindowMessageProcedure(nint windowHandle, uint message, nint wordParameter, nint longParameter)
    {
        return message switch
        {
            NonClientHitTestMessage => new nint(TransparentHitTestResult),
            MouseActivateMessage => new nint(DoNotActivateResult),
            _ => DefWindowProc(windowHandle, message, wordParameter, longParameter)
        };
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint windowHandle, uint message, nint wordParameter, nint longParameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public nint WindowProcedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint BackgroundBrush;
        public nint MenuName;
        public nint ClassName;
        public nint SmallIcon;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true)]
    private static partial nint GetModuleHandle(nint moduleName);

    [LibraryImport("gdi32.dll")]
    private static partial nint GetStockObject(int objectIndex);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static partial ushort RegisterClassEx(ref WindowClass windowClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(
        uint extendedStyle,
        nint className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint windowHandle, uint message, nint wordParameter, nint longParameter);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtr(nint windowHandle, int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(
        nint windowHandle,
        uint colorKey,
        byte alpha,
        uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLayeredWindowAttributes(
        nint windowHandle,
        out uint colorKey,
        out byte alpha,
        out uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RedrawWindow(nint windowHandle, nint updateRectangle, nint updateRegion, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint windowHandle);
}
