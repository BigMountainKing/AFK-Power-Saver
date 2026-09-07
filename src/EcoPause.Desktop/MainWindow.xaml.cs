using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AFKPowerSaver.OverlayModel;
using AFKPowerSaver.Overlay.Windows;
using AFKPowerSaver.PowerPlan;
using EcoPause.Desktop.Safety;
using EcoPause.Hardware.Abstractions;
using Microsoft.Win32;

namespace EcoPause.Desktop;

public partial class MainWindow : Window, IDisposable
{
    private static readonly Brush ReadyBrush = new SolidColorBrush(Color.FromRgb(103, 232, 165));
    private static readonly Brush RunningBrush = new SolidColorBrush(Color.FromRgb(96, 165, 250));
    private static readonly Brush StoppedBrush = new SolidColorBrush(Color.FromRgb(251, 191, 36));
    private static readonly Brush FailedBrush = new SolidColorBrush(Color.FromRgb(248, 113, 113));

    private readonly WindowsActivityObserver _activityObserver;
    private readonly ProfileCoordinator _profile;
    private readonly RecoveryRetrySchedule _recoveryRetry = new();
    private readonly CpuRecoveryCompanion _cpuCompanion = new();
    private readonly DispatcherTimer _screenDimmingTopmostTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(200)
    };
    private DesktopUserSettings _settings = DesktopUserSettings.Default;
    private GlobalHotKeyRegistration? _activationHotKey;
    private ScreenDimmingSession? _screenDimmingSession;
    private TrayIconController? _trayIcon;
    private bool _isRunning;
    private bool _isProbeRunning;
    private bool _isInitializing = true;
    private bool _isClosingBroker;
    private bool _allowClose;
    private bool _exitRequested;
    private bool _disposed;
    private bool _isConfiguringGpuControls = true;
    private bool _isConfiguringDimmingControls = true;
    private bool _isConfiguringCpuControls = true;
    private bool _isConfiguringIdleControls = true;
    private bool _isConfiguringStartupControls = true;
    private bool _startupRegistrationAvailable = true;
    private bool _cpuLimitActive;
    private bool _cpuRecoveryBlocked;
    private bool _idleTimerOwnsActiveLimit;
    private bool _idleTransitionAttempted;
    private bool? _lastIdleUnattended;
    private IReadOnlyList<DisplaySelectionOption> _displaySelectionOptions = [];
    private PausePreviewReason? _lastActivityReason;
    private ReadOnlyGpuSnapshot? _liveGpu;
    private LiveSessionProcessController? _liveSession;
    private TipPaymentConfiguration? _tipPayments;
    private DesktopLiveToggleAvailability _liveToggleAvailability = new(
        false,
        DesktopLiveToggleIntent.Unavailable,
        ActiveProfile: null,
        "Waiting for strict live GPU preflight.");

    public MainWindow()
    {
        _profile = new ProfileCoordinator(new DesktopProfileOperations(this));
        InitializeComponent();
        InitializeTipOptions();
        _settings = DesktopUserSettingsStore.Load();
        GpuLimitCheckBox.IsChecked = _settings.GpuLimitEnabled;
        _isConfiguringGpuControls = false;
        LivePowerSlider.Value = _settings.PowerPercentage;
        CpuLimitCheckBox.IsChecked = _settings.CpuLimitEnabled;
        CpuLimitSlider.Value = _settings.CpuMaximumPercent;
        _isConfiguringCpuControls = false;
        IdleActivationCheckBox.IsChecked = _settings.IdleActivationEnabled;
        IdleMinutesSlider.Value = _settings.IdleActivationMinutes;
        _isConfiguringIdleControls = false;
        try
        {
            LaunchOnStartupCheckBox.IsChecked = StartupLaunchRegistration.IsEnabled();
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or System.Security.SecurityException or
            IOException or InvalidOperationException or ArgumentException)
        {
            _startupRegistrationAvailable = false;
            LaunchOnStartupCheckBox.IsChecked = false;
        }
        _isConfiguringStartupControls = false;
        ScreenDimmingCheckBox.IsChecked = _settings.ScreenDimmingEnabled;
        OverlayOpacitySlider.Value = _settings.OverlayOpacityPercent;
        RefreshDisplayTargetOptions(saveFallback: false);
        _isConfiguringDimmingControls = false;
        HotKeyModifiersComboBox.ItemsSource = HotKeyOptions.Modifiers;
        HotKeyKeyComboBox.ItemsSource = HotKeyOptions.Keys;
        if (_settings.HotKeyUserConfigured)
        {
            HotKeyModifiersComboBox.SelectedItem = HotKeyOptions.Modifiers.Single(option =>
                option.Value == _settings.HotKey.Modifiers);
            HotKeyKeyComboBox.SelectedItem = HotKeyOptions.Keys.Single(option =>
                option.VirtualKey == _settings.HotKey.VirtualKey);
        }
        else
        {
            HotKeyModifiersComboBox.SelectedIndex = -1;
            HotKeyKeyComboBox.SelectedIndex = -1;
        }
        _activityObserver = new WindowsActivityObserver();
        _activityObserver.SnapshotChanged += ActivityObserver_SnapshotChanged;
        _screenDimmingTopmostTimer.Tick += ScreenDimmingTopmostTimer_Tick;
        SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        UpdateScreenDimmingUi();
        UpdateCpuLimitUi();
        UpdateIdleActivationUi();
        UpdateStartupLaunchUi();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeTrayIcon();
        RegisterActivationHotKey();
        _activityObserver.Start();
        AppendOutput(_settings.IdleActivationEnabled
            ? $"Idle activation enabled: apply after {_settings.IdleActivationMinutes} minute(s), restore on resumed activity."
            : "Idle activation is off; button and hotkey remain available.");
        await RecoverPendingCpuAtStartupAsync();
        await InitializeLiveSessionAsync();
        _isInitializing = false;
        UpdateControlAvailability();
        await RefreshGpuAsync();
    }

    private async Task InitializeLiveSessionAsync()
    {
        SetStatus("Approve startup permission for live hotkey control…", RunningBrush);
        AppendOutput("Starting the normal-user live-session launcher.");
        AppendOutput("Approve one UAC prompt now; no per-hotkey confirmation or UAC prompt will follow.");
        try
        {
            var (controller, result) = await LiveSessionProcessController.StartAsync();
            _liveSession = controller;
            if (!string.IsNullOrWhiteSpace(result.StartupTranscript))
            {
                AppendOutput(result.StartupTranscript.TrimEnd());
            }

            if (result.Succeeded && controller is not null)
            {
                AppendOutput(result.Message);
                SetStatus("Startup live session authorized and ready", ReadyBrush);
            }
            else
            {
                AppendOutput(result.Message);
                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    AppendOutput($"Live-session startup detail: {result.Error}");
                }

                SetStatus("Live session unavailable — live controls disabled", StoppedBrush);
            }
        }
#pragma warning disable CA1031 // Startup authorization failure must leave the normal UI usable.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            AppendOutput($"Live-session startup stopped safely: {exception.Message}");
            SetStatus("Live session unavailable — live controls disabled", StoppedBrush);
        }

        UpdateHotKeyStatusForLiveAction();
    }

    private async void RefreshGpuButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshGpuAsync();

    private void LivePowerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized)
        {
            return;
        }

        if (_liveGpu is not null)
        {
            _settings = _settings with { PowerPercentage = SelectedPowerPercentage };
            TrySaveSettings();
        }
        UpdateLiveTargetUi();
        UpdateControlAvailability();
    }

    private void GpuLimitControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_isConfiguringGpuControls)
        {
            return;
        }

        _settings = _settings with { GpuLimitEnabled = GpuLimitCheckBox.IsChecked == true };
        TrySaveSettings();
        UpdateLiveTargetUi();
        UpdateControlAvailability();
    }

    private async void CpuLimitControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_isConfiguringCpuControls)
        {
            return;
        }

        var enabled = CpuLimitCheckBox.IsChecked == true;
        _settings = _settings with { CpuLimitEnabled = enabled };
        TrySaveSettings();
        if (_liveToggleAvailability.Intent == DesktopLiveToggleIntent.Restore ||
            (!enabled && ProcessorLimitRecoveryJournal.Exists()))
        {
            _isRunning = true;
            UpdateControlAvailability();
            try
            {
                if (enabled)
                {
                    await ApplyCpuLimitAsync();
                }
                else
                {
                    await RestoreCpuLimitAsync("CPU limiting disabled; exact original AC/DC values restored.");
                }
            }
#pragma warning disable CA1031 // A CPU boundary failure must remain visible and recoverable.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                AppendOutput($"CPU limit update stopped safely: {exception.Message}");
                SetStatus("CPU state not verified — restart to prioritize recovery", FailedBrush);
            }
            finally
            {
                _isRunning = false;
            }
        }

        UpdateCpuLimitUi();
        UpdateLiveTargetUi();
        UpdateControlAvailability();
    }

    private void CpuLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CpuLimitValueText is not null)
        {
            CpuLimitValueText.Text = $"{SelectedCpuLimitPercent}%";
        }
        if (_isConfiguringCpuControls)
        {
            return;
        }

        _settings = _settings with { CpuMaximumPercent = SelectedCpuLimitPercent };
        TrySaveSettings();
        UpdateCpuLimitUi();
        UpdateLiveTargetUi();
    }

    private void ScreenDimmingControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_isConfiguringDimmingControls)
        {
            return;
        }

        _settings = _settings with { ScreenDimmingEnabled = ScreenDimmingCheckBox.IsChecked == true };
        TrySaveSettings();
        if (!_settings.ScreenDimmingEnabled)
        {
            HideScreenDimming("Screen dimming disabled while the GPU limit remains unchanged.");
        }
        else if (_liveToggleAvailability.Intent == DesktopLiveToggleIntent.Restore)
        {
            TryActivateScreenDimming();
        }

        UpdateScreenDimmingUi();
        UpdateLiveTargetUi();
        UpdateControlAvailability();
    }

    private void OverlayOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OverlayOpacityValueText is not null)
        {
            OverlayOpacityValueText.Text = $"{SelectedOverlayOpacityPercent}%";
        }
        if (_isConfiguringDimmingControls)
        {
            return;
        }

        _settings = _settings with { OverlayOpacityPercent = SelectedOverlayOpacityPercent };
        TrySaveSettings();
        if (_screenDimmingSession is not null)
        {
            RecreateScreenDimming("Screen dimming opacity updated.");
        }

        UpdateScreenDimmingUi();
    }

    private void DisplayTargetOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_isConfiguringDimmingControls || sender is not System.Windows.Controls.CheckBox checkBox ||
            checkBox.DataContext is not DisplaySelectionOption changed)
        {
            return;
        }

        var previousConfigurationState = _isConfiguringDimmingControls;
        _isConfiguringDimmingControls = true;
        try
        {
            if (changed.IsSelected && changed.IsAllDisplays)
            {
                foreach (var option in _displaySelectionOptions.Where(option => !option.IsAllDisplays))
                {
                    option.IsSelected = false;
                }
            }
            else if (changed.IsSelected)
            {
                var allDisplays = _displaySelectionOptions.SingleOrDefault(option => option.IsAllDisplays);
                if (allDisplays is not null)
                {
                    allDisplays.IsSelected = false;
                }
            }
        }
        finally
        {
            _isConfiguringDimmingControls = previousConfigurationState;
        }

        var selectedIds = SelectedDisplayTargetIds;
        _settings = _settings with
        {
            DisplaySelectionConfigured = true,
            OverlayTargetIds = selectedIds,
            OverlayTargetId = selectedIds.FirstOrDefault() ?? string.Empty
        };
        TrySaveSettings();
        if (_screenDimmingSession is not null)
        {
            RecreateScreenDimming("Screen dimming display target updated.");
        }

        UpdateScreenDimmingUi();
        UpdateLiveTargetUi();
        UpdateControlAvailability();
    }

    private async void IdleActivationControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_isConfiguringIdleControls)
        {
            return;
        }

        _settings = _settings with { IdleActivationEnabled = IdleActivationCheckBox.IsChecked == true };
        _idleTransitionAttempted = false;
        _lastIdleUnattended = null;
        TrySaveSettings();
        UpdateIdleActivationUi();
        UpdateControlAvailability();

        if (!_settings.IdleActivationEnabled &&
            _idleTimerOwnsActiveLimit &&
            CanRunIdleTransition(DesktopLiveToggleIntent.Restore))
        {
            await ExecuteIdleDirectiveAsync(IdleActivationDirective.RestoreOriginal);
        }
    }

    private void IdleMinutesSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IdleMinutesValueText is not null)
        {
            IdleMinutesValueText.Text = $"{SelectedIdleMinutes} min";
        }
        if (_isConfiguringIdleControls)
        {
            return;
        }

        _settings = _settings with { IdleActivationMinutes = SelectedIdleMinutes };
        _idleTransitionAttempted = false;
        _lastIdleUnattended = null;
        TrySaveSettings();
        UpdateIdleActivationUi();
        UpdateControlAvailability();
    }

    private void LaunchOnStartupControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_isConfiguringStartupControls)
        {
            return;
        }

        var enabled = LaunchOnStartupCheckBox.IsChecked == true;
        try
        {
            StartupLaunchRegistration.SetEnabled(enabled);
            _startupRegistrationAvailable = true;
            AppendOutput(enabled
                ? "Windows startup launch enabled for the current user."
                : "Windows startup launch disabled for the current user.");
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or System.Security.SecurityException or
            IOException or InvalidOperationException or ArgumentException)
        {
            _startupRegistrationAvailable = false;
            _isConfiguringStartupControls = true;
            LaunchOnStartupCheckBox.IsChecked = false;
            _isConfiguringStartupControls = false;
            AppendOutput($"Windows startup setting could not be changed: {exception.Message}");
        }

        UpdateStartupLaunchUi();
    }

    private void UpdateStartupLaunchUi()
    {
        StartupStatusText.Text = !_startupRegistrationAvailable
            ? "Unavailable"
            : LaunchOnStartupCheckBox.IsChecked == true
                ? "On · launches after sign-in"
                : "Off";
        StartupStatusText.Foreground = _startupRegistrationAvailable
            ? LaunchOnStartupCheckBox.IsChecked == true ? ReadyBrush : StoppedBrush
            : FailedBrush;
    }

    private void SupportButton_Click(object sender, RoutedEventArgs e) =>
        SupportPopup.IsOpen = !SupportPopup.IsOpen;

    private void InitializeTipOptions()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("AFKPowerSaver.PaymentInfo.txt")
                ?? throw new InvalidOperationException("The embedded tip configuration is unavailable.");
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            _tipPayments = TipPaymentPolicy.Parse(reader.ReadToEnd());
            OpenPayPalTipButton.Tag = _tipPayments.PayPalUri;
            CryptoTipItemsControl.ItemsSource = _tipPayments.CryptoOptions;
            CryptoTipSection.Visibility = _tipPayments.CryptoOptions.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            PaymentTipStatusText.Text = "AFK Power Saver does not process or save payment information";
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or DecoderFallbackException)
        {
            OpenPayPalTipButton.IsEnabled = false;
            CryptoTipSection.Visibility = Visibility.Collapsed;
            PaymentTipStatusText.Text = "Tip options are temporarily unavailable because their configuration did not validate.";
        }
    }

    private void OpenPayPalTipButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tipPayments?.PayPalUri is not { } uri)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            MessageBox.Show(
                this,
                "Windows could not open the PayPal link in your default browser.",
                "AFK Power Saver tip link",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void CopyCryptoAddressButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string address } button ||
            _tipPayments?.CryptoOptions.Any(option =>
                string.Equals(option.Address, address, StringComparison.Ordinal)) != true)
        {
            return;
        }

        try
        {
            Clipboard.SetText(address);
            button.Content = "Copied";
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException)
        {
            MessageBox.Show(
                this,
                "Windows could not copy the address. Please try again.",
                "AFK Power Saver clipboard",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void HotKeySelection_Changed(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateControlAvailability();

    private void ApplyHotKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (HotKeyModifiersComboBox.SelectedItem is not HotKeyModifierOption modifiers ||
            HotKeyKeyComboBox.SelectedItem is not HotKeyKeyOption key)
        {
            return;
        }

        ReplaceActivationHotKey(
            new HotKeyDefinition(modifiers.Value, key.VirtualKey, key.DisplayName),
            userInitiated: true);
    }

    private async void LiveToggleButton_Click(object sender, RoutedEventArgs e) =>
        await RunConfiguredProfileToggleAsync(DesktopActivationSource.AmberLiveButton);

    private async void ActivationHotKey_Pressed(object? sender, EventArgs e) =>
        await RunConfiguredProfileToggleAsync(DesktopActivationSource.GlobalHotKey);

    private void InitializeTrayIcon()
    {
        if (_trayIcon is not null)
        {
            return;
        }

        _trayIcon = new TrayIconController();
        _trayIcon.OpenRequested += TrayIcon_OpenRequested;
        _trayIcon.ToggleProfileRequested += TrayIcon_ToggleProfileRequested;
        _trayIcon.RefreshGpuRequested += TrayIcon_RefreshGpuRequested;
        _trayIcon.ExitRequested += TrayIcon_ExitRequested;
        _trayIcon.GpuEnabledChanged += TrayIcon_GpuEnabledChanged;
        _trayIcon.CpuEnabledChanged += TrayIcon_CpuEnabledChanged;
        _trayIcon.DisplayEnabledChanged += TrayIcon_DisplayEnabledChanged;
        _trayIcon.IdleEnabledChanged += TrayIcon_IdleEnabledChanged;
        _trayIcon.CloseButtonPreferenceChanged += TrayIcon_CloseButtonPreferenceChanged;
        UpdateTrayIconState();
    }

    internal void RunTraySmokeTest()
    {
        InitializeTrayIcon();
        _trayIcon?.Update(new TrayIconState(
            ProfileActive: false,
            CanToggleProfile: true,
            CanEditFeatures: true,
            CanRefreshGpu: true,
            GpuEnabled: true,
            CpuEnabled: true,
            DisplayEnabled: true,
            IdleEnabled: true,
            CloseButtonPreference.MinimizeToTray,
            "Tray lifecycle smoke test"));
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private void TrayIcon_OpenRequested(object? sender, EventArgs e) =>
        _ = Dispatcher.BeginInvoke(ShowMainWindow);

    private void TrayIcon_ToggleProfileRequested(object? sender, EventArgs e) =>
        _ = Dispatcher.BeginInvoke(async () =>
            await RunConfiguredProfileToggleAsync(DesktopActivationSource.TrayMenu));

    private void TrayIcon_RefreshGpuRequested(object? sender, EventArgs e) =>
        _ = Dispatcher.BeginInvoke(async () => await RefreshGpuAsync());

    private void TrayIcon_ExitRequested(object? sender, EventArgs e) =>
        _ = Dispatcher.BeginInvoke(() =>
        {
            _exitRequested = true;
            Close();
        });

    private void TrayIcon_GpuEnabledChanged(bool enabled) =>
        _ = Dispatcher.BeginInvoke(() => GpuLimitCheckBox.IsChecked = enabled);

    private void TrayIcon_CpuEnabledChanged(bool enabled) =>
        _ = Dispatcher.BeginInvoke(() => CpuLimitCheckBox.IsChecked = enabled);

    private void TrayIcon_DisplayEnabledChanged(bool enabled) =>
        _ = Dispatcher.BeginInvoke(() => ScreenDimmingCheckBox.IsChecked = enabled);

    private void TrayIcon_IdleEnabledChanged(bool enabled) =>
        _ = Dispatcher.BeginInvoke(() => IdleActivationCheckBox.IsChecked = enabled);

    private void TrayIcon_CloseButtonPreferenceChanged(CloseButtonPreference preference) =>
        _ = Dispatcher.BeginInvoke(() =>
        {
            _settings = _settings with { CloseButtonPreference = preference };
            TrySaveSettings();
            UpdateTrayIconState();
        });

    private void ShowMainWindow()
    {
        if (!IsVisible)
        {
            Show();
        }
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        _ = Activate();
    }

    private void MinimizeToTray()
    {
        Hide();
        AppendOutput("AFK Power Saver is running in the notification area; hotkey and idle activation remain available.");
        UpdateTrayIconState();
    }

    private bool HasEnabledLocalProfile => _settings.CpuLimitEnabled ||
        (_settings.ScreenDimmingEnabled && SelectedDisplayTargetIds.Length > 0);

    private bool IsConfiguredProfileActive => _profile.IsActive || _cpuLimitActive ||
        ProcessorLimitRecoveryJournal.Exists() || _screenDimmingSession is not null;

    private async Task RunConfiguredProfileToggleAsync(DesktopActivationSource source)
    {
        if (_isRunning || _isProbeRunning || _isInitializing || _isClosingBroker) return;
        if (source != DesktopActivationSource.IdleObserver) _recoveryRetry.Reset();
        _profile.AdoptRecovery(false, ProcessorLimitRecoveryJournal.Exists());
        var restoring = IsConfiguredProfileActive;
        if (!restoring && (_cpuRecoveryBlocked ||
            (_settings.GpuLimitEnabled && (_liveSession is not { IsReady: true } ||
            !_liveToggleAvailability.Available || !ValidateSelectedLiveTarget().Accepted))))
        {
            SetStatus("Activation unavailable; restore pending settings first", FailedBrush);
            return;
        }
        _isRunning = true;
        UpdateControlAvailability();
        RunProgress.Visibility = Visibility.Visible;
        OutputTextBox.Clear();
        AppendOutput($"Activation trigger: {DesktopActivationRoutingPolicy.Describe(source)}.");
        try
        {
            var result = restoring
                ? await _profile.RestoreAsync()
                : await _profile.ApplyAsync(new(_settings.GpuLimitEnabled, _settings.CpuLimitEnabled,
                    _settings.ScreenDimmingEnabled && SelectedDisplayTargetIds.Length > 0));
            foreach (var error in result.Errors) AppendOutput(error);
            SetStatus(result.Succeeded
                ? result.Active ? "Configured limits applied and verified" : "Original settings restored and verified"
                : result.Active ? "Restoration pending — use Restore original to retry" : "Activation failed; partial changes restored",
                result.Succeeded ? result.Active ? StoppedBrush : ReadyBrush : FailedBrush);
            if (!result.Active)
            {
                _idleTimerOwnsActiveLimit = false;
                _recoveryRetry.Reset();
            }
        }
        finally
        {
            _isRunning = false;
            RunProgress.Visibility = Visibility.Collapsed;
            await RefreshGpuAsync();
            UpdateLiveTargetUi();
            UpdateControlAvailability();
            UpdateIdleActivationUi();
        }
    }

    private sealed class DesktopProfileOperations(MainWindow window) : IProfileOperations
    {
        public async Task<GpuOperationResult> ApplyGpuAsync() =>
            Validate(await (window._liveSession ?? throw new InvalidOperationException("GPU session unavailable."))
                .ToggleAsync(window.SelectedLiveTarget));
        public async Task<GpuOperationResult> RestoreGpuAsync()
        {
            if (window._liveSession is not { IsReady: true })
            {
                if (window._liveSession is { } previous)
                {
                    try { await previous.DisposeAsync(); }
                    catch (Exception ex) { window.AppendOutput("Previous GPU session: " + ex.Message); }
                }
                await window.InitializeLiveSessionAsync();
            }
            return Validate(await (window._liveSession ?? throw new InvalidOperationException("GPU recovery connection was not authorized."))
                .RestoreAsync());
        }
        private GpuOperationResult Validate(LiveSessionToggleResult result)
        {
            if (!string.IsNullOrWhiteSpace(result.Transcript)) window.AppendOutput(result.Transcript.TrimEnd());
            if (!result.Succeeded || result.Result is not { IsValid: true })
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Message : result.Error);
            return result.Result;
        }
        public Task ApplyCpuAsync() => window.ApplyCpuLimitAsync();
        public Task RestoreCpuAsync() => window.RestoreCpuLimitAsync("Exact CPU AC/DC values restored.");
        public void ShowDimming()
        {
            window.TryActivateScreenDimming();
            if (window._screenDimmingSession is null) throw new InvalidOperationException("Display dimming failed.");
        }
        public void HideDimming() => window.HideScreenDimming("Display dimming removed.");
    }
    private void RegisterActivationHotKey() =>
        ReplaceActivationHotKey(_settings.HotKey, userInitiated: false);

    private void ReplaceActivationHotKey(HotKeyDefinition definition, bool userInitiated)
    {
        var previous = _activationHotKey?.Definition;
        if (_activationHotKey is not null)
        {
            _activationHotKey.Pressed -= ActivationHotKey_Pressed;
            _activationHotKey.Dispose();
            _activationHotKey = null;
        }

        try
        {
            var windowHandle = new WindowInteropHelper(this).Handle;
            _activationHotKey = new GlobalHotKeyRegistration(windowHandle, definition);
            _activationHotKey.Pressed += ActivationHotKey_Pressed;
            _settings = _settings with
            {
                HotKey = definition,
                HotKeyUserConfigured = _settings.HotKeyUserConfigured || userInitiated
            };
            TrySaveSettings();
            UpdateHotKeyStatusForLiveAction();
            AppendOutput($"Global live-toggle hotkey ready: {definition.DisplayName}.");
        }
        catch (Win32Exception exception)
        {
            if (previous is not null && previous != definition)
            {
                try
                {
                    var windowHandle = new WindowInteropHelper(this).Handle;
                    _activationHotKey = new GlobalHotKeyRegistration(windowHandle, previous);
                    _activationHotKey.Pressed += ActivationHotKey_Pressed;
                }
                catch (Win32Exception)
                {
                    _activationHotKey = null;
                }
            }

            HotKeyStatusText.Text = $"{definition.DisplayName} unavailable · button still works";
            HotKeyStatusText.Foreground = FailedBrush;
            AppendOutput($"{definition.DisplayName} is already in use. The previous hotkey was preserved when possible.");
            if (userInitiated)
            {
                MessageBox.Show(
                    this,
                    exception.Message,
                    "Hotkey unavailable",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
    }

    private void TrySaveSettings()
    {
        try
        {
            DesktopUserSettingsStore.Save(_settings);
        }
#pragma warning disable CA1031 // Settings persistence must not interrupt GPU safety behavior.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            AppendOutput($"Settings were not saved: {exception.Message}");
        }
    }

    private int SelectedCpuLimitPercent => (int)Math.Round(CpuLimitSlider.Value);

    private async Task RecoverPendingCpuAtStartupAsync()
    {
        if (!ProcessorLimitRecoveryJournal.Exists())
        {
            AppendOutput("CPU recovery preflight: no pending exact restoration.");
            UpdateCpuLimitUi();
            return;
        }

        SetStatus("Recovering pending CPU power-plan state…", RunningBrush);
        AppendOutput("Pending CPU recovery journal detected; exact restoration is running before new limits are allowed.");
        try
        {
            var restored = await Task.Run(WindowsProcessorLimitLifecycle.RestorePendingAndVerify);
            _cpuLimitActive = false;
            _cpuRecoveryBlocked = false;
            AppendOutput(
                $"CPU startup recovery: PASS · AC {restored?.AcMaximumPercent}% / DC {restored?.DcMaximumPercent}% restored exactly.");
        }
#pragma warning disable CA1031 // Startup must remain visible if exact CPU recovery cannot complete.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _cpuRecoveryBlocked = true;
            AppendOutput($"CPU startup recovery remains pending: {exception.Message}");
            SetStatus("CPU exact recovery required — live controls blocked", FailedBrush);
        }

        UpdateCpuLimitUi();
    }

    private async Task ApplyCpuLimitAsync()
    {
        // WPF controls are UI-thread-affine. Capture the value before entering Task.Run.
        var requestedPercent = SelectedCpuLimitPercent;
        try
        {
            await _cpuCompanion.EnsureReadyAsync();
            var result = await Task.Run(() =>
                WindowsProcessorLimitLifecycle.ApplyAndVerify(requestedPercent));
            _cpuLimitActive = result.Changed;
            _cpuRecoveryBlocked = false;
            if (result.Changed)
            {
                AppendOutput(
                    $"CPU ceiling applied and verified: AC {result.Applied.AcMaximumPercent}% / DC {result.Applied.DcMaximumPercent}%. " +
                    $"Exact original AC {result.Original.AcMaximumPercent}% / DC {result.Original.DcMaximumPercent}% retained for restoration.");
            }
            else
            {
                AppendOutput(
                    $"CPU ceiling required no change: active plan was already at or below {requestedPercent}% on AC and DC.");
            }
        }
        catch
        {
            _cpuRecoveryBlocked = ProcessorLimitRecoveryJournal.Exists();
            throw;
        }
        finally
        {
            UpdateCpuLimitUi();
        }
    }

    private async Task RestoreCpuLimitAsync(string successMessage)
    {
        try
        {
            var restored = await Task.Run(WindowsProcessorLimitLifecycle.RestorePendingAndVerify);
            _cpuLimitActive = false;
            _cpuRecoveryBlocked = false;
            if (restored is not null)
            {
                AppendOutput($"{successMessage} AC {restored.AcMaximumPercent}% / DC {restored.DcMaximumPercent}%.");
            }
        }
        catch
        {
            _cpuRecoveryBlocked = ProcessorLimitRecoveryJournal.Exists();
            throw;
        }
        finally
        {
            UpdateCpuLimitUi();
        }
    }

    private void UpdateCpuLimitUi()
    {
        CpuLimitValueText.Text = $"{SelectedCpuLimitPercent}%";
        if (_cpuRecoveryBlocked)
        {
            CpuLimitStatusText.Text = "Exact CPU restoration pending · restart to retry recovery";
            CpuLimitStatusText.Foreground = FailedBrush;
        }
        else if (_cpuLimitActive || ProcessorLimitRecoveryJournal.Exists())
        {
            CpuLimitStatusText.Text = "CPU ceiling active · exact AC/DC restoration pending";
            CpuLimitStatusText.Foreground = StoppedBrush;
        }
        else if (_settings.CpuLimitEnabled)
        {
            CpuLimitStatusText.Text = $"Enabled · {SelectedCpuLimitPercent}% applies with the live GPU limit";
            CpuLimitStatusText.Foreground = ReadyBrush;
        }
        else
        {
            CpuLimitStatusText.Text = "Off";
            CpuLimitStatusText.Foreground = (Brush)FindResource("SecondaryText");
        }
    }

    private int SelectedOverlayOpacityPercent => (int)Math.Round(OverlayOpacitySlider.Value);

    private string[] SelectedDisplayTargetIds => _displaySelectionOptions
        .Where(option => option.IsSelected)
        .Select(option => option.Id)
        .ToArray();

    private string SelectedDisplaySummary
    {
        get
        {
            var selected = _displaySelectionOptions.Where(option => option.IsSelected).ToArray();
            if (selected.Length == 0)
            {
                return "Select display";
            }
            if (selected.Any(option => option.IsAllDisplays))
            {
                return "All displays";
            }
            return selected.Length == 1
                ? selected[0].DisplayName
                : $"{selected.Length} displays selected";
        }
    }

    private DisplayTargetOption SelectedDisplayTarget => new(
        SelectedDisplayTargetIds.FirstOrDefault() ?? string.Empty,
        SelectedDisplaySummary,
        SelectedDisplayTargetIds.Contains(DisplayTargetPolicy.AllDisplaysTargetId, StringComparer.OrdinalIgnoreCase));

    private void RefreshDisplayTargetOptions(bool saveFallback)
    {
        var options = DisplayTargetCatalog.GetOptions();
        var normalizedTargetIds = DisplayTargetPolicy.NormalizeSavedTargets(
            _settings.DisplaySelectionConfigured ? _settings.OverlayTargetIds : [],
            options.Where(option => !option.IsAllDisplays).Select(option => option.Id));
        var fellBack = _settings.DisplaySelectionConfigured &&
            !normalizedTargetIds.SequenceEqual(
                _settings.OverlayTargetIds ?? [],
                StringComparer.OrdinalIgnoreCase);

        var previousConfigurationState = _isConfiguringDimmingControls;
        _isConfiguringDimmingControls = true;
        try
        {
            var selected = normalizedTargetIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _displaySelectionOptions = options
                .Select(option => new DisplaySelectionOption(
                    option.Id,
                    option.DisplayName,
                    option.IsAllDisplays,
                    selected.Contains(option.Id)))
                .ToArray();
            DisplayTargetItemsControl.ItemsSource = _displaySelectionOptions;
            if (fellBack)
            {
                _settings = _settings with
                {
                    OverlayTargetIds = normalizedTargetIds,
                    OverlayTargetId = normalizedTargetIds.FirstOrDefault() ?? string.Empty
                };
            }
        }
        finally
        {
            _isConfiguringDimmingControls = previousConfigurationState;
        }

        if (fellBack && saveFallback)
        {
            TrySaveSettings();
            AppendOutput("One or more saved displays are no longer connected and were removed from the dimming selection.");
        }

        DisplayTargetButton.Content = SelectedDisplaySummary;
    }

    private void TryActivateScreenDimming()
    {
        HideScreenDimming(logMessage: null);
        if (!_settings.ScreenDimmingEnabled)
        {
            UpdateScreenDimmingUi();
            return;
        }

        try
        {
            var selectedTargetIds = SelectedDisplayTargetIds;
            if (selectedTargetIds.Length == 0)
            {
                ScreenDimmingStatusText.Text = "Select at least one display before activation";
                ScreenDimmingStatusText.Foreground = FailedBrush;
                return;
            }
            var session = ScreenDimmingSession.Show(
                _settings.OverlayOpacityPercent,
                selectedTargetIds);
            _screenDimmingSession = session;
            if (!session.ResolvedTargetIds.SequenceEqual(
                selectedTargetIds,
                StringComparer.OrdinalIgnoreCase))
            {
                _settings = _settings with
                {
                    OverlayTargetIds = session.ResolvedTargetIds.ToArray(),
                    OverlayTargetId = session.ResolvedTargetIds.Count > 0
                        ? session.ResolvedTargetIds[0]
                        : string.Empty
                };
                RefreshDisplayTargetOptions(saveFallback: true);
            }

            _screenDimmingTopmostTimer.Start();
            ScreenDimmingStatusText.Text =
                $"Active · {session.OpacityPercent}% · {session.DisplayCount} display(s) · click-through";
            ScreenDimmingStatusText.Foreground = ReadyBrush;
            AppendOutput(
                $"Native screen dimming active: {session.OpacityPercent}% on {SelectedDisplaySummary}. Click-through verified at creation.");
        }
#pragma warning disable CA1031 // Overlay failure must leave the GPU lifecycle usable and visible.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _screenDimmingSession?.Dispose();
            _screenDimmingSession = null;
            _screenDimmingTopmostTimer.Stop();
            ScreenDimmingStatusText.Text = $"Dimming stopped safely · {exception.Message}";
            ScreenDimmingStatusText.Foreground = FailedBrush;
            AppendOutput($"Screen dimming did not start; the verified GPU state is unchanged: {exception.Message}");
        }
    }

    private void RecreateScreenDimming(string reason)
    {
        HideScreenDimming(logMessage: null);
        AppendOutput(reason);
        TryActivateScreenDimming();
    }

    private void HideScreenDimming(string? logMessage)
    {
        _screenDimmingTopmostTimer.Stop();
        var wasActive = _screenDimmingSession is not null;
        _screenDimmingSession?.Dispose();
        _screenDimmingSession = null;
        if (wasActive && !string.IsNullOrWhiteSpace(logMessage))
        {
            AppendOutput(logMessage);
        }

        UpdateScreenDimmingUi();
    }

    private void UpdateScreenDimmingUi()
    {
        OverlayOpacityValueText.Text = $"{SelectedOverlayOpacityPercent}%";
        DisplayTargetButton.Content = SelectedDisplaySummary;
        if (_screenDimmingSession is not null)
        {
            ScreenDimmingStatusText.Text =
                $"Active · {_screenDimmingSession.OpacityPercent}% · {_screenDimmingSession.DisplayCount} display(s)";
            ScreenDimmingStatusText.Foreground = ReadyBrush;
        }
        else if (_settings.ScreenDimmingEnabled)
        {
            ScreenDimmingStatusText.Text = $"Ready · {SelectedDisplayTarget.DisplayName}";
            ScreenDimmingStatusText.Foreground = StoppedBrush;
        }
        else
        {
            ScreenDimmingStatusText.Text = "Screen dimming is off";
            ScreenDimmingStatusText.Foreground = new SolidColorBrush(Color.FromRgb(158, 176, 194));
        }
    }

    private void ScreenDimmingTopmostTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            _screenDimmingSession?.ReassertTopmost();
        }
        catch (Exception exception) when (exception is Win32Exception or ObjectDisposedException)
        {
            HideScreenDimming(logMessage: null);
            ScreenDimmingStatusText.Text = $"Dimming stopped safely · {exception.Message}";
            ScreenDimmingStatusText.Foreground = FailedBrush;
            AppendOutput($"Screen dimming stopped safely while the GPU limit remains recoverable: {exception.Message}");
        }
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            var wasActive = _screenDimmingSession is not null;
            RefreshDisplayTargetOptions(saveFallback: true);
            if (wasActive)
            {
                RecreateScreenDimming("Windows display layout changed; screen dimming targets were revalidated.");
            }
            else
            {
                UpdateScreenDimmingUi();
            }
        });
    }

    private void UpdateRegisteredHotKeyStatus(string message)
    {
        if (_activationHotKey is not null)
        {
            HotKeyStatusText.Text = message;
        }
    }

    private void UpdateHotKeyStatusForLiveAction()
    {
        if (_activationHotKey is null)
        {
            return;
        }

        var sessionReady = _liveSession is { IsReady: true };
        if (!_settings.GpuLimitEnabled)
        {
            var localReady = HasEnabledLocalProfile && !_cpuRecoveryBlocked;
            HotKeyStatusText.Foreground = localReady ? StoppedBrush : FailedBrush;
            HotKeyStatusText.Text = localReady
                ? $"{_settings.HotKey.DisplayName} ready - GPU limiting off"
                : "Hotkey registered - select at least one enabled limit";
            return;
        }
        HotKeyStatusText.Foreground = sessionReady && _liveToggleAvailability.Available && !_cpuRecoveryBlocked
            ? StoppedBrush
            : FailedBrush;
        if (!sessionReady)
        {
            HotKeyStatusText.Text = "Hotkey registered · startup live session unavailable";
            return;
        }
        if (_cpuRecoveryBlocked)
        {
            HotKeyStatusText.Text = "Hotkey blocked · exact CPU restoration required";
            return;
        }

        var cpuSuffix = _settings.CpuLimitEnabled ? $" + CPU {SelectedCpuLimitPercent}%" : string.Empty;
        HotKeyStatusText.Text = _liveToggleAvailability.Intent switch
        {
            DesktopLiveToggleIntent.Pause when ValidateSelectedLiveTarget().Accepted =>
                $"{_settings.HotKey.DisplayName} ready · GPU {SelectedLiveTarget.Percentage}%{cpuSuffix}",
            DesktopLiveToggleIntent.Restore =>
                $"{_settings.HotKey.DisplayName} ready · restore original state",
            _ => "Hotkey registered · live preflight unavailable"
        };
    }

    private void SetStatus(string text, Brush indicator)
    {
        StatusText.Text = text;
        StatusDot.Fill = indicator;
    }

    private async Task RefreshGpuAsync()
    {
        if (_isProbeRunning || _isRunning)
        {
            return;
        }

        _isProbeRunning = true;
        UpdateControlAvailability();
        RefreshGpuButton.Content = "Refreshing read-only data…";

        try
        {
            var result = await ReadOnlyGpuProbeRunner.RunAsync();
            if (!result.Ready || result.Devices.Count == 0)
            {
                ShowGpuUnavailable(result.Message);
                return;
            }

            var gpu = result.Devices[0];
            _liveGpu = gpu;
            var minimumPercentage = DesktopLiveTargetPolicy.GetMinimumPercentage(gpu);
            var step = Math.Max(1, gpu.RelativePercentageStep);
            var maximumPercentage = DesktopLiveTargetPolicy.MaximumPercentage -
                (DesktopLiveTargetPolicy.MaximumPercentage - minimumPercentage) % step;
            var requestedPercentage = Math.Clamp(
                _settings.PowerPercentage,
                minimumPercentage,
                maximumPercentage);
            var snappedPercentage = requestedPercentage -
                (requestedPercentage - minimumPercentage) % step;
            LivePowerSlider.Minimum = minimumPercentage;
            LivePowerSlider.Maximum = maximumPercentage;
            LivePowerSlider.TickFrequency = step;
            LivePowerSlider.Value = snappedPercentage;
            LivePowerRangeText.Text = gpu.LimitKind == GpuPowerLimitKind.DefaultRelativePercentage
                ? $"{minimumPercentage}–{maximumPercentage}% of factory · AMD driver adjustment"
                : $"{minimumPercentage}–{maximumPercentage}% · {gpu.MinimumLimitWatts:0.##}–{gpu.DefaultLimitWatts:0.##} W default-relative";
            var availability = DesktopLiveTogglePolicy.Evaluate(result);
            UpdateLiveToggleAvailability(availability);
            GpuLimitText.Text = FormatGpuLimit(gpu, gpu.CurrentLimitWatts);
            GpuNameText.Text = gpu.LimitKind == GpuPowerLimitKind.DefaultRelativePercentage
                ? $"{gpu.Model} · AMD ADLX · experimental"
                : $"{gpu.Model} · driver power limit";
            GpuUsageText.Text = gpu.CurrentUsageWatts is null
                ? "Draw unavailable"
                : $"Draw {gpu.CurrentUsageWatts.Value:0.##} W";
            FooterGpuUsageText.Text = gpu.CurrentUsageWatts is null
                ? "GPU power usage unavailable"
                : $"GPU power usage: {gpu.CurrentUsageWatts.Value:0.##} W";
            GpuRangeText.Text = gpu.LimitKind == GpuPowerLimitKind.DefaultRelativePercentage
                ? $"Range {gpu.MinimumLimitWatts:0.##}–{gpu.MaximumLimitWatts:0.##}% of factory"
                : $"Range {gpu.MinimumLimitWatts:0.##}–{gpu.MaximumLimitWatts:0.##} W";
            AppendOutput(
                $"Read-only GPU refresh: {gpu.Model}, limit {FormatGpuLimit(gpu, gpu.CurrentLimitWatts)}. No settings changed.");
        }
#pragma warning disable CA1031 // Read-only probe failure must degrade the card without crashing the UI.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            ShowGpuUnavailable("Live GPU data is unavailable.");
            AppendOutput($"Read-only GPU refresh stopped safely: {exception.Message}");
        }
        finally
        {
            _isProbeRunning = false;
            RefreshGpuButton.Content = "Refresh GPU data";
            UpdateControlAvailability();
        }
    }

    private void ShowGpuUnavailable(string message)
    {
        _liveGpu = null;
        UpdateLiveToggleAvailability(new DesktopLiveToggleAvailability(
            false,
            DesktopLiveToggleIntent.Unavailable,
            ActiveProfile: null,
            "Strict live GPU preflight is unavailable."));
        GpuLimitText.Text = "— W";
        GpuNameText.Text = message;
        GpuUsageText.Text = "Draw unavailable";
        FooterGpuUsageText.Text = "GPU power usage unavailable";
        GpuRangeText.Text = "Range unavailable";
        LivePowerRangeText.Text = "GPU-relative range unavailable";
    }

    private void UpdateLiveToggleAvailability(DesktopLiveToggleAvailability availability)
    {
        _liveToggleAvailability = availability;
        if (availability.Intent == DesktopLiveToggleIntent.Restore && availability.ActiveProfile is not null)
        {
            LivePowerSlider.Value = availability.ActiveProfile.Percentage;
        }

        UpdateLiveTargetUi();
        UpdateControlAvailability();
    }

    private int SelectedPowerPercentage => (int)Math.Round(LivePowerSlider.Value);

    private DesktopLiveTargetOption SelectedLiveTarget =>
        _liveGpu is null
            ? new DesktopLiveTargetOption(SelectedPowerPercentage, 0)
            : DesktopLiveTargetPolicy.ResolvePercentage(SelectedPowerPercentage, _liveGpu);

    private DesktopLiveTargetValidationResult ValidateSelectedLiveTarget() =>
        _liveGpu is null
            ? new DesktopLiveTargetValidationResult(false, "Live GPU constraints are unavailable.")
            : DesktopLiveTargetPolicy.Validate(SelectedLiveTarget, _liveGpu);

    private void UpdateLiveTargetUi()
    {
        if (IsConfiguredProfileActive)
        {
            LiveToggleButton.Content = "RESTORE ORIGINAL";
            LiveToggleStatusText.Text = "Restore each active resource independently. Failed restorations can be retried here.";
            LiveToggleStatusText.Foreground = StoppedBrush;
            return;
        }
        if (!_settings.GpuLimitEnabled)
        {
            LiveTargetValueText.Text = "GPU OFF";
            LiveToggleButton.Content = IsConfiguredProfileActive ? "RESTORE ORIGINAL" : "ACTIVATE LIMITS";
            var localParts = new List<string>();
            if (_settings.CpuLimitEnabled)
            {
                localParts.Add($"CPU {SelectedCpuLimitPercent}%");
            }
            if (_settings.ScreenDimmingEnabled && SelectedDisplayTargetIds.Length > 0)
            {
                localParts.Add($"display {SelectedOverlayOpacityPercent}%");
            }
            LiveToggleStatusText.Text = localParts.Count == 0
                ? "GPU limiting is off. Enable CPU limiting or display dimming to activate a profile."
                : $"Ready: {string.Join(" + ", localParts)}. GPU power will not change.";
            LiveToggleStatusText.Foreground = localParts.Count == 0 ? FailedBrush : StoppedBrush;
            UpdateHotKeyStatusForLiveAction();
            return;
        }
        var selected = SelectedLiveTarget;
        LiveTargetValueText.Text = _liveGpu is null
            ? $"{SelectedPowerPercentage}%"
            : selected.LimitKind == GpuPowerLimitKind.DefaultRelativePercentage
                ? selected.FormattedLimit
                : $"{selected.Percentage}% · {selected.FormattedLimit}";
        if (_liveSession is not { IsReady: true })
        {
            LiveToggleButton.Content = "Startup live session unavailable";
            LiveToggleStatusText.Text = "Restart AFK Power Saver and approve the startup UAC prompt to enable live controls.";
            LiveToggleStatusText.Foreground = FailedBrush;
            UpdateHotKeyStatusForLiveAction();
            return;
        }
        if (_cpuRecoveryBlocked)
        {
            LiveToggleButton.Content = "CPU restoration required";
            LiveToggleStatusText.Text = "Restart the app or disable CPU limiting to retry exact AC/DC restoration.";
            LiveToggleStatusText.Foreground = FailedBrush;
            UpdateHotKeyStatusForLiveAction();
            return;
        }

        switch (_liveToggleAvailability.Intent)
        {
            case DesktopLiveToggleIntent.Pause:
                var validation = ValidateSelectedLiveTarget();
                var dimmingSuffix = _settings.ScreenDimmingEnabled ? " + dim" : string.Empty;
                var cpuSuffix = _settings.CpuLimitEnabled ? $" + CPU {SelectedCpuLimitPercent}%" : string.Empty;
                LiveToggleButton.Content = "ACTIVATE LIMITS";
                LiveToggleStatusText.Text = validation.Accepted
                    ? $"Ready: GPU {selected.Percentage}%{cpuSuffix}{dimmingSuffix}. Use the button, hotkey, or optional idle timer."
                    : validation.Message;
                LiveToggleStatusText.Foreground = validation.Accepted ? StoppedBrush : FailedBrush;
                break;
            case DesktopLiveToggleIntent.Restore:
                LiveToggleButton.Content = "RESTORE ORIGINAL";
                LiveToggleStatusText.Text = _liveToggleAvailability.Message;
                LiveToggleStatusText.Foreground = StoppedBrush;
                break;
            default:
                LiveToggleButton.Content = "Live toggle unavailable";
                LiveToggleStatusText.Text = _liveToggleAvailability.Message;
                LiveToggleStatusText.Foreground = FailedBrush;
                break;
        }

        UpdateHotKeyStatusForLiveAction();
    }

    private async void ActivityObserver_SnapshotChanged(object? sender, WindowsActivitySnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => ActivityObserver_SnapshotChanged(sender, snapshot));
            return;
        }

        if (!snapshot.Available)
        {
            ActivityDot.Fill = FailedBrush;
            ActivityStatusText.Text = "Windows activity monitor unavailable";
            ActivityDetailText.Text = "Button and hotkey remain available";
            return;
        }

        var threshold = TimeSpan.FromMinutes(SelectedIdleMinutes);
        var decision = PausePreviewPolicy.Evaluate(
            snapshot.IdleDuration,
            snapshot.SessionLocked,
            threshold);

        if (_lastIdleUnattended != decision.WouldPause)
        {
            _idleTransitionAttempted = false;
            _lastIdleUnattended = decision.WouldPause;
        }

        UpdateActivityStatus(snapshot, decision);

        if (_lastActivityReason != decision.Reason)
        {
            AppendOutput(_settings.IdleActivationEnabled
                ? decision.WouldPause
                    ? $"Idle activation threshold reached ({decision.Reason})."
                    : "User activity detected; idle-owned limits will restore automatically."
                : decision.WouldPause
                    ? $"Idle preview reached ({decision.Reason}); automatic activation is off."
                    : "Activity preview: active input detected; automatic activation is off.");
            _lastActivityReason = decision.Reason;
        }

        var directive = IdleActivationPolicy.Evaluate(
            _settings.IdleActivationEnabled,
            _idleTimerOwnsActiveLimit,
            _idleTransitionAttempted,
            decision.WouldPause,
            CanRunIdleTransition(DesktopLiveToggleIntent.Pause),
            CanRunIdleTransition(DesktopLiveToggleIntent.Restore));
        if (directive != IdleActivationDirective.None)
        {
            await ExecuteIdleDirectiveAsync(directive);
        }
    }

    private void UpdateActivityStatus(
        WindowsActivitySnapshot snapshot,
        PausePreviewDecision decision)
    {
        if (!_settings.IdleActivationEnabled)
        {
            ActivityDot.Fill = RunningBrush;
            ActivityStatusText.Text = "Idle activation off";
            ActivityDetailText.Text = $"Preview: {FormatDuration(snapshot.IdleDuration)} idle / {SelectedIdleMinutes}:00 threshold";
            return;
        }

        if (_idleTimerOwnsActiveLimit)
        {
            ActivityDot.Fill = StoppedBrush;
            ActivityStatusText.Text = decision.WouldPause
                ? "Idle limits active"
                : "Activity detected - restoring original state";
            ActivityDetailText.Text = "Only the timer-owned limit will restore automatically";
            return;
        }

        if (IsConfiguredProfileActive)
        {
            ActivityDot.Fill = StoppedBrush;
            ActivityStatusText.Text = "Manual limits active";
            ActivityDetailText.Text = "Mouse activity will not restore a manual button or hotkey limit";
            return;
        }

        if (decision.WouldPause)
        {
            ActivityDot.Fill = StoppedBrush;
            ActivityStatusText.Text = "Idle threshold reached";
            ActivityDetailText.Text = decision.Reason == PausePreviewReason.SessionLocked
                ? "Windows session locked - applying configured limits"
                : $"Idle {FormatDuration(decision.IdleDuration)} - applying configured limits";
            return;
        }

        ActivityDot.Fill = RunningBrush;
        ActivityStatusText.Text = $"Active use - limits in {FormatDuration(decision.RemainingUntilPause)}";
        ActivityDetailText.Text = $"Idle {FormatDuration(decision.IdleDuration)} - restores automatically when activity resumes";
    }

    private bool CanRunIdleTransition(DesktopLiveToggleIntent requiredIntent)
    {
        if (_isRunning || _isProbeRunning || _isInitializing || _isClosingBroker) return false;
        if (requiredIntent == DesktopLiveToggleIntent.Restore)
            return IsConfiguredProfileActive && _recoveryRetry.IsDue;
        if (IsConfiguredProfileActive || _cpuRecoveryBlocked) return false;
        return _settings.GpuLimitEnabled
            ? _liveSession is { IsReady: true } && _liveToggleAvailability.Available &&
                _liveToggleAvailability.Intent == DesktopLiveToggleIntent.Pause && ValidateSelectedLiveTarget().Accepted
            : HasEnabledLocalProfile;
    }
    private async Task ExecuteIdleDirectiveAsync(IdleActivationDirective directive)
    {
        var requiredIntent = directive == IdleActivationDirective.ApplyLimits
            ? DesktopLiveToggleIntent.Pause
            : DesktopLiveToggleIntent.Restore;
        if (!CanRunIdleTransition(requiredIntent))
        {
            return;
        }

        if (directive == IdleActivationDirective.ApplyLimits) _idleTransitionAttempted = true;
        AppendOutput(directive == IdleActivationDirective.ApplyLimits
            ? $"Enabled idle timer is applying the configured profile after {SelectedIdleMinutes} minute(s)."
            : "Enabled idle timer is restoring the original state after user activity resumed.");
        await RunConfiguredProfileToggleAsync(DesktopActivationSource.IdleObserver);

        if (directive == IdleActivationDirective.ApplyLimits)
        {
            _idleTimerOwnsActiveLimit = IsConfiguredProfileActive;
            AppendOutput(_idleTimerOwnsActiveLimit
                ? "Idle activation completed; this timer-owned state will restore when activity resumes."
                : "Idle activation did not complete; automatic restoration ownership was not claimed.");
        }
        else if (IsConfiguredProfileActive)
        {
            _recoveryRetry.Failed();
            if (_recoveryRetry.Exhausted)
            {
                ShowMainWindow();
                SetStatus("Automatic recovery could not finish — use Restore original to retry", FailedBrush);
            }
        }
        else if (!IsConfiguredProfileActive)
        {
            _idleTimerOwnsActiveLimit = false;
            AppendOutput("Idle-owned limits restored to their exact original state.");
        }

        UpdateIdleActivationUi();
    }

    private int SelectedIdleMinutes => (int)Math.Round(IdleMinutesSlider.Value);

    private void UpdateIdleActivationUi()
    {
        IdleMinutesValueText.Text = $"{SelectedIdleMinutes} min";
        IdleActivationStatusText.Text = _settings.IdleActivationEnabled
            ? _idleTimerOwnsActiveLimit
                ? "Active - restores on activity"
                : "On - lock triggers immediately"
            : "Off - manual triggers only";
        IdleActivationStatusText.Foreground = _settings.IdleActivationEnabled
            ? ReadyBrush
            : (Brush)FindResource("SecondaryText");
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var totalSeconds = Math.Max(0, (int)Math.Ceiling(duration.TotalSeconds));
        return $"{totalSeconds / 60}:{totalSeconds % 60:00}";
    }

    private void UpdateControlAvailability()
    {
        var available = !_isRunning && !_isProbeRunning && !_isInitializing && !_isClosingBroker;
        var liveSessionReady = _liveSession is { IsReady: true };
        var liveTargetAccepted = _liveToggleAvailability.Intent != DesktopLiveToggleIntent.Pause ||
            ValidateSelectedLiveTarget().Accepted;
        var gpuPathRequired = _settings.GpuLimitEnabled;
        var gpuActionReady = liveSessionReady && _liveToggleAvailability.Available && liveTargetAccepted;
        var localActionReady = IsConfiguredProfileActive || HasEnabledLocalProfile;
        LiveToggleButton.IsEnabled = available && (IsConfiguredProfileActive || (!_cpuRecoveryBlocked &&
            (gpuPathRequired ? gpuActionReady : localActionReady)));
        RefreshGpuButton.IsEnabled = available;
        LivePowerSlider.IsEnabled =
            available && _settings.GpuLimitEnabled && liveSessionReady &&
            _liveToggleAvailability.Intent == DesktopLiveToggleIntent.Pause && !IsConfiguredProfileActive;
        HotKeyModifiersComboBox.IsEnabled = available;
        HotKeyKeyComboBox.IsEnabled = available;
        ApplyHotKeyButton.IsEnabled = available &&
            HotKeyModifiersComboBox.SelectedItem is HotKeyModifierOption &&
            HotKeyKeyComboBox.SelectedItem is HotKeyKeyOption;
        GpuLimitCheckBox.IsEnabled = available && !IsConfiguredProfileActive;
        CpuLimitCheckBox.IsEnabled = available && !IsConfiguredProfileActive;
        CpuLimitSlider.IsEnabled = available && !_cpuRecoveryBlocked && _settings.CpuLimitEnabled &&
            !IsConfiguredProfileActive;
        ScreenDimmingCheckBox.IsEnabled = available && !IsConfiguredProfileActive;
        OverlayOpacitySlider.IsEnabled = available && _settings.ScreenDimmingEnabled && !IsConfiguredProfileActive;
        DisplayTargetButton.IsEnabled = available && _settings.ScreenDimmingEnabled && !IsConfiguredProfileActive;
        IdleActivationCheckBox.IsEnabled = available;
        IdleMinutesSlider.IsEnabled = available && _settings.IdleActivationEnabled;
        UpdateTrayIconState();
    }

    private void UpdateTrayIconState()
    {
        if (_trayIcon is null)
        {
            return;
        }

        var profileActive = IsConfiguredProfileActive;
        var busy = _isRunning || _isProbeRunning || _isInitializing || _isClosingBroker;
        _trayIcon.Update(new TrayIconState(
            profileActive,
            LiveToggleButton.IsEnabled,
            !busy && !profileActive,
            RefreshGpuButton.IsEnabled,
            _settings.GpuLimitEnabled,
            _settings.CpuLimitEnabled,
            _settings.ScreenDimmingEnabled,
            _settings.IdleActivationEnabled,
            _settings.CloseButtonPreference,
            busy
                ? "A safety action is in progress"
                : profileActive
                    ? "Configured limits are active"
                    : "Original settings are active"));
    }

    private void AppendOutput(string line)
    {
        OutputTextBox.AppendText(line + Environment.NewLine);
        OutputTextBox.ScrollToEnd();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (string.Equals(
            Environment.GetEnvironmentVariable("AFKPOWERSAVER_CLOSE_SMOKE_TEST"),
            "1",
            StringComparison.Ordinal) || string.Equals(
            Environment.GetEnvironmentVariable("ECOPAUSE_CLOSE_SMOKE_TEST"),
            "1",
            StringComparison.Ordinal))
        {
            _allowClose = true;
            return;
        }

        if (_allowClose)
        {
            return;
        }
        if (_isInitializing)
        {
            e.Cancel = true;
            SetStatus("Wait for startup recovery to finish before exiting", StoppedBrush);
            return;
        }

        if (!_exitRequested)
        {
            var closeChoice = _settings.CloseButtonPreference switch
            {
                CloseButtonPreference.MinimizeToTray => CloseDialogChoice.MinimizeToTray,
                CloseButtonPreference.ExitApplication => CloseDialogChoice.ExitApplication,
                _ => ShowClosePreferenceDialog()
            };

            if (closeChoice == CloseDialogChoice.MinimizeToTray)
            {
                e.Cancel = true;
                MinimizeToTray();
                return;
            }
            if (closeChoice == CloseDialogChoice.Cancel)
            {
                e.Cancel = true;
                return;
            }
            _exitRequested = true;
        }

        HideScreenDimming("Screen dimming removed before application shutdown.");

        var cpuRecoveryPending = _cpuLimitActive || ProcessorLimitRecoveryJournal.Exists();
        if (!_isRunning && !_isClosingBroker && (_liveSession is not null || cpuRecoveryPending || _profile.IsActive))
        {
            e.Cancel = true;
            _isClosingBroker = true;
            SetStatus("Restoring CPU and closing the GPU broker…", RunningBrush);
            UpdateControlAvailability();
            var session = _liveSession;
            _liveSession = null;
            Exception? shutdownFailure = null;
            _profile.AdoptRecovery(false, cpuRecoveryPending);
            // Keep the session connected while the coordinator restores its GPU ownership.
            _liveSession = session;
            var restoration = await _profile.RestoreAsync();
            if (!restoration.Succeeded)
                shutdownFailure = new InvalidOperationException(string.Join("; ", restoration.Errors));
            try { await _cpuCompanion.DisposeAsync(); }
            catch (Exception ex) { shutdownFailure = ex; AppendOutput(ex.Message); }
            session = _liveSession;
            _liveSession = null;
            if (session is not null)
            {
                try
                {
                    await session.DisposeAsync();
                }
#pragma warning disable CA1031 // A failed broker shutdown must remain visible and keep the window open.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    shutdownFailure = shutdownFailure is null
                        ? exception
                        : new AggregateException(shutdownFailure, exception);
                    AppendOutput($"Startup broker shutdown was not verified: {exception.Message}");
                }
            }

            if (shutdownFailure is null)
            {
                _allowClose = true;
                Close();
            }
            else
            {
                ShowMainWindow();
                SetStatus("Restoration not fully verified — check CPU and GPU state", FailedBrush);
                _isClosingBroker = false;
                MessageBox.Show(
                    this,
                    $"AFK Power Saver could not verify every restoration. Check that the GPU is at {FormatGpuLimit(_liveGpu, _liveGpu?.DefaultLimitWatts)} and restart the app to retry any pending CPU recovery before closing.",
                    "AFK Power Saver restoration check required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return;
        }

        if (!_isRunning && !_isClosingBroker)
        {
            _allowClose = true;
            return;
        }

        e.Cancel = true;
        if (!_isClosingBroker)
        {
            MessageBox.Show(
                this,
                "Wait for the current safety lifecycle to finish before closing AFK Power Saver.",
                "AFK Power Saver safety action in progress",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private static string FormatGpuLimit(ReadOnlyGpuSnapshot? gpu, decimal? value)
    {
        if (gpu is null || value is null)
        {
            return "its original setting";
        }

        return gpu.LimitKind == GpuPowerLimitKind.DefaultRelativePercentage
            ? $"{value.Value:0.##}% of factory"
            : $"{value.Value:0.##} W";
    }

    private CloseDialogChoice ShowClosePreferenceDialog()
    {
        var dialog = new ClosePreferenceDialog { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return CloseDialogChoice.Cancel;
        }

        if (dialog.RememberChoice)
        {
            _settings = _settings with
            {
                CloseButtonPreference = dialog.Choice == CloseDialogChoice.MinimizeToTray
                    ? CloseButtonPreference.MinimizeToTray
                    : CloseButtonPreference.ExitApplication
            };
            TrySaveSettings();
        }
        return dialog.Choice;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        Dispose();
        Application.Current.Shutdown(0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        _screenDimmingTopmostTimer.Stop();
        _screenDimmingTopmostTimer.Tick -= ScreenDimmingTopmostTimer_Tick;
        _screenDimmingSession?.Dispose();
        _screenDimmingSession = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
        if (_activationHotKey is not null)
        {
            _activationHotKey.Pressed -= ActivationHotKey_Pressed;
            _activationHotKey.Dispose();
        }

        _activityObserver.SnapshotChanged -= ActivityObserver_SnapshotChanged;
        _activityObserver.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal sealed class DisplaySelectionOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public DisplaySelectionOption(string id, string displayName, bool isAllDisplays, bool isSelected)
    {
        Id = id;
        DisplayName = displayName;
        IsAllDisplays = isAllDisplays;
        _isSelected = isSelected;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public bool IsAllDisplays { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
