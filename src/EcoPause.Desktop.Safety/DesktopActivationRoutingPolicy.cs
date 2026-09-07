namespace EcoPause.Desktop.Safety;

public enum DesktopActivationSource
{
    GreenFakeButton,
    AmberLiveButton,
    GlobalHotKey,
    IdleObserver,
    TrayMenu
}

public enum DesktopActivationRoute
{
    PersistentFakeToggle,
    ConfirmedLiveToggle,
    PreviewOnly
}

public static class DesktopActivationRoutingPolicy
{
    public static DesktopActivationRoute Route(DesktopActivationSource source) =>
        source switch
        {
            DesktopActivationSource.GreenFakeButton => DesktopActivationRoute.PersistentFakeToggle,
            DesktopActivationSource.AmberLiveButton or
                DesktopActivationSource.GlobalHotKey or
                DesktopActivationSource.IdleObserver or
                DesktopActivationSource.TrayMenu =>
                DesktopActivationRoute.ConfirmedLiveToggle,
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };

    public static string Describe(DesktopActivationSource source) =>
        source switch
        {
            DesktopActivationSource.GreenFakeButton => "green fake button",
            DesktopActivationSource.AmberLiveButton => "primary activation button",
            DesktopActivationSource.GlobalHotKey => "assigned global hotkey",
            DesktopActivationSource.IdleObserver => "enabled idle activation timer",
            DesktopActivationSource.TrayMenu => "notification-area menu",
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
}
