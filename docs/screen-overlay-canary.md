# Screen overlay canary

AFK Power Saver 1.17 adds an isolated screen-dimming test. It does not change display brightness, CPU settings, GPU settings, or the main desktop application's limit state.

Version 1.17.1 replaces the WPF overlay surfaces with native layered windows. Their alpha, topmost, click-through, and no-activate styles are applied at creation and read back before the test continues. This corrects the first 1.17 build, where WPF could rewrite those native flags.

## Qualified behavior

- A black overlay is created for every display Windows reports.
- Opacity is selectable from 10% through 95% in 5% increments.
- Overlay windows are topmost, excluded from the taskbar, do not activate, and pass mouse input through to the underlying windows.
- `Ctrl+Shift+F12` dismisses all overlays globally.
- Every test dismisses itself after 15 seconds.
- The test button remains disabled if the emergency hotkey cannot be registered.
- The overlay closes instead of continuing if Windows cannot apply and read back the click-through safety styles.
- While active, the controller periodically reasserts the native overlay above shell surfaces such as the taskbar without activating it.

## Manual test

1. Open `AFK Power Saver Overlay Test.exe`.
2. Confirm that the displayed monitor count matches the connected displays you expect to dim.
3. Leave opacity at 70% for the first run.
4. Select **Run 15-second overlay test**.
5. While the screen is dimmed, click and interact with an application underneath it. The overlay must not intercept those clicks.
6. Press `Ctrl+Shift+F12`. Every monitor must return immediately.
7. Run the test again and do not press the hotkey. Every monitor must return automatically after 15 seconds.
8. Try a second opacity to confirm the slider changes the darkness.

## Current limitations

This qualification app is separate from the main limit toggle. It has not yet been connected to the assigned production hotkey. A conventional topmost overlay may appear behind some exclusive full-screen applications; borderless-windowed and ordinary desktop windows are the primary qualification targets.
