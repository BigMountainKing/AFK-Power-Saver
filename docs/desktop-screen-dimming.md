# Desktop screen-dimming integration

AFK Power Saver 1.18.1 integrates the qualified native overlay with the existing startup-authorized live GPU limit. The 1.18.1 Windows package also corrects the companion executable payloads required for GPU probing and the startup elevation broker.

## Controls

The main window provides:

- an explicit **Dim the selected display while limited** switch;
- 10-95% black-overlay opacity in 5% increments;
- **All monitors** plus one entry for each display Windows currently reports;
- primary-monitor and resolution labels.

The feature is off by default for existing installations. Its enabled state, opacity, and display selection are saved with the existing hotkey and GPU percentage settings.

## Lifecycle

The overlay is created only after the elevated broker has applied and verified the requested GPU percentage. The same amber button or assigned global hotkey removes the overlay immediately, then requests and verifies exact GPU restoration.

Closing AFK Power Saver removes every overlay before shutting down the elevated broker. Disabling dimming while limited removes the overlay without changing the protected GPU recovery state.

If display layout changes while dimming is active, the native overlays are recreated against fresh bounds. A disconnected saved display falls back to **All monitors**, and that fallback is persisted.

The WPF desktop contains no GPU setter. Native screen dimming remains normal-user, click-through, no-activate, and separate from privileged hardware control.

## Manual test

1. Start `AFK Power Saver.exe` and approve its one startup UAC prompt.
2. Enable screen dimming, select a monitor or **All monitors**, and choose an opacity.
3. Use the amber live button or assigned hotkey to apply the GPU limit.
4. Confirm the selected display(s) dim only after the GPU limit is verified.
5. Confirm mouse and keyboard input still reach applications beneath the overlay.
6. Use the same button or hotkey again. The overlay should disappear immediately and the GPU should restore exactly.
7. Repeat once using the other display-selection mode when multiple monitors are available.
