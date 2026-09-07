using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace EcoPause.Desktop;

internal sealed record TrayIconState(
    bool ProfileActive,
    bool CanToggleProfile,
    bool CanEditFeatures,
    bool CanRefreshGpu,
    bool GpuEnabled,
    bool CpuEnabled,
    bool DisplayEnabled,
    bool IdleEnabled,
    CloseButtonPreference CloseButtonPreference,
    string StatusText);

internal sealed class TrayIconController : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ToolStripMenuItem _statusItem;
    private readonly WinForms.ToolStripMenuItem _toggleProfileItem;
    private readonly WinForms.ToolStripMenuItem _gpuItem;
    private readonly WinForms.ToolStripMenuItem _cpuItem;
    private readonly WinForms.ToolStripMenuItem _displayItem;
    private readonly WinForms.ToolStripMenuItem _idleItem;
    private readonly WinForms.ToolStripMenuItem _refreshItem;
    private readonly WinForms.ToolStripMenuItem _askCloseItem;
    private readonly WinForms.ToolStripMenuItem _minimizeCloseItem;
    private readonly WinForms.ToolStripMenuItem _exitCloseItem;
    private bool _updating;
    private bool _disposed;

    public TrayIconController()
    {
        _statusItem = new WinForms.ToolStripMenuItem("Starting…") { Enabled = false };
        var openItem = new WinForms.ToolStripMenuItem("Open AFK Power Saver");
        openItem.Font = new Font(openItem.Font, FontStyle.Bold);
        openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        _toggleProfileItem = new WinForms.ToolStripMenuItem("Activate configured limits");
        _toggleProfileItem.Click += (_, _) => ToggleProfileRequested?.Invoke(this, EventArgs.Empty);

        _gpuItem = CreateFeatureItem("GPU limit", enabled => GpuEnabledChanged?.Invoke(enabled));
        _cpuItem = CreateFeatureItem("CPU limit", enabled => CpuEnabledChanged?.Invoke(enabled));
        _displayItem = CreateFeatureItem("Display dimming", enabled => DisplayEnabledChanged?.Invoke(enabled));
        _idleItem = CreateFeatureItem("Idle activation", enabled => IdleEnabledChanged?.Invoke(enabled));
        var featuresItem = new WinForms.ToolStripMenuItem("Enabled features");
        featuresItem.DropDownItems.AddRange([_gpuItem, _cpuItem, _displayItem, _idleItem]);

        _refreshItem = new WinForms.ToolStripMenuItem("Refresh GPU data");
        _refreshItem.Click += (_, _) => RefreshGpuRequested?.Invoke(this, EventArgs.Empty);

        _askCloseItem = CreateCloseBehaviorItem("Ask when X is clicked", CloseButtonPreference.Ask);
        _minimizeCloseItem = CreateCloseBehaviorItem("Always minimise to tray", CloseButtonPreference.MinimizeToTray);
        _exitCloseItem = CreateCloseBehaviorItem("Always exit and restore", CloseButtonPreference.ExitApplication);
        var closeBehaviorItem = new WinForms.ToolStripMenuItem("Close button behaviour");
        closeBehaviorItem.DropDownItems.AddRange([_askCloseItem, _minimizeCloseItem, _exitCloseItem]);

        var exitItem = new WinForms.ToolStripMenuItem("Exit AFK Power Saver");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.AddRange([
            openItem,
            _statusItem,
            new WinForms.ToolStripSeparator(),
            _toggleProfileItem,
            featuresItem,
            _refreshItem,
            new WinForms.ToolStripSeparator(),
            closeBehaviorItem,
            new WinForms.ToolStripSeparator(),
            exitItem
        ]);

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "AFK Power Saver",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? ToggleProfileRequested;
    public event EventHandler? RefreshGpuRequested;
    public event EventHandler? ExitRequested;
    public event Action<bool>? GpuEnabledChanged;
    public event Action<bool>? CpuEnabledChanged;
    public event Action<bool>? DisplayEnabledChanged;
    public event Action<bool>? IdleEnabledChanged;
    public event Action<CloseButtonPreference>? CloseButtonPreferenceChanged;

    public void Update(TrayIconState state)
    {
        _updating = true;
        try
        {
            _statusItem.Text = state.StatusText;
            _toggleProfileItem.Text = state.ProfileActive
                ? "Restore original settings"
                : "Activate configured limits";
            _toggleProfileItem.Enabled = state.CanToggleProfile;
            _gpuItem.Checked = state.GpuEnabled;
            _cpuItem.Checked = state.CpuEnabled;
            _displayItem.Checked = state.DisplayEnabled;
            _idleItem.Checked = state.IdleEnabled;
            _gpuItem.Enabled = state.CanEditFeatures;
            _cpuItem.Enabled = state.CanEditFeatures;
            _displayItem.Enabled = state.CanEditFeatures;
            _idleItem.Enabled = state.CanEditFeatures;
            _refreshItem.Enabled = state.CanRefreshGpu;
            _askCloseItem.Checked = state.CloseButtonPreference == CloseButtonPreference.Ask;
            _minimizeCloseItem.Checked = state.CloseButtonPreference == CloseButtonPreference.MinimizeToTray;
            _exitCloseItem.Checked = state.CloseButtonPreference == CloseButtonPreference.ExitApplication;
            _notifyIcon.Text = state.ProfileActive
                ? "AFK Power Saver — limits active"
                : "AFK Power Saver — original settings";
        }
        finally
        {
            _updating = false;
        }
    }

    private WinForms.ToolStripMenuItem CreateFeatureItem(string text, Action<bool> changed)
    {
        var item = new WinForms.ToolStripMenuItem(text) { CheckOnClick = true };
        item.CheckedChanged += (_, _) =>
        {
            if (!_updating)
            {
                changed(item.Checked);
            }
        };
        return item;
    }

    private WinForms.ToolStripMenuItem CreateCloseBehaviorItem(string text, CloseButtonPreference preference)
    {
        var item = new WinForms.ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            if (!_updating)
            {
                CloseButtonPreferenceChanged?.Invoke(preference);
            }
        };
        return item;
    }

    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var background = new SolidBrush(Color.FromArgb(18, 54, 41));
            using var border = new Pen(Color.FromArgb(45, 212, 191), 2f);
            graphics.FillEllipse(background, 2, 2, 28, 28);
            graphics.DrawEllipse(border, 2, 2, 28, 28);
            using var bolt = new SolidBrush(Color.FromArgb(103, 232, 165));
            graphics.FillPolygon(bolt,
            [
                new PointF(17, 6),
                new PointF(10, 17),
                new PointF(15, 17),
                new PointF(13, 26),
                new PointF(23, 13),
                new PointF(17, 13)
            ]);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
    }
}
