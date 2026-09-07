# Desktop safety preview and live read-only data

Milestone 1.0 introduces EcoPause's first Windows desktop window. Milestone 1.1 adds live, sanitized GPU measurements, milestone 1.2 adds a read-only Windows activity-policy preview, milestone 1.3 adds a global shortcut, milestone 1.4 initially makes that shortcut a protected persistent fake toggle, and milestone 1.5 adds constrained fake target profiles. Milestone 1.9 adds one separately confirmed amber manual live action, milestone 1.10 adds a distinct allow-listed live-target selector, and milestone 1.11 routes the hotkey through that confirmed live action. The green button and simulated selector remain fake-only; idle remains preview-only.

The visible Windows run has been verified: the WPF layout rendered without clipping, the single-UAC fake lifecycle completed, the evidence gate accepted the complete transcript, and the window displayed the exact restored 450 W state.

## Safety boundaries

- `EcoPause.Desktop.exe` declares `asInvoker` and stops if it detects an administrator token.
- The desktop assembly references only its safety parsers. It has no runtime reference to Core, NVIDIA, the helper protocol, named-pipe transport, or control assembly; probe and launcher project references are build-order-only.
- Live measurements come from the existing normal-user read-only probe in a separate process with bounded output, a ten-second timeout, and a strict field allow-list.
- GPU UUIDs, serial numbers, driver identifiers, raw native output, and other unexpected fields are rejected rather than displayed.
- The activity panel reads only aggregate idle duration and session lock/unlock state. It never records input content or history.
- The five-minute decision is display-only and has no connection to the helper launcher.
- `Ctrl+Shift+F3` and the amber button share one guarded, confirmed live-toggle method; the green button retains the separate fake method.
- Holding the shortcut cannot create repeated runs, and an occupied shortcut fails visibly without disabling the button.
- Pressing the green button starts the fake launcher; the amber button or hotkey can start only the qualified normal-user live launcher after confirmation.
- The launcher—not the UI—creates the pipe, starts the UAC helper, authenticates commands, and performs automatic fake restoration.
- The button is disabled during a run, and the window refuses to close until the child lifecycle finishes.
- Declining UAC is displayed as a safe cancellation.
- Exit code zero is insufficient for success. The UI requires all twelve safety evidence lines from the launcher transcript.

## Run it

From a normal Visual Studio terminal:

```powershell
dotnet run --project src/EcoPause.Desktop
```

The window should show:

- a prominent `MANUAL LIVE · UAC` label and a visually separate amber live action;
- the live GPU model, driver power limit, current draw, and permitted range;
- a `LIVE · READ ONLY` label and a manual refresh button;
- a live activity countdown or `Would pause now — preview only` decision;
- a permanent `AUTO ACTION OFF` label;
- a visible `CTRL + SHIFT + F3` activation shortcut and its registration status;
- an allow-listed fake target selector with live driver-range validation;
- one state-aware **Activate/Restore persistent fake pause** button;
- a read-only safety log.

Press the button and approve the single UAC prompt. The log should stream the same milestone 0.8 evidence previously verified in the console. A successful run ends with the green status:

```text
Fake lifecycle restored and verified
```

The GPU card reads real measurements. The lifecycle action changes only the in-memory fake GPU and still requires complete no-NVIDIA evidence before it can show success.

## Development verification

The solution includes a noninteractive WPF construction/layout smoke path and transcript tests for:

- complete evidence acceptance;
- incomplete evidence rejection;
- safe UAC-declined classification;
- sanitized probe-data acceptance;
- rejection of unknown identifying fields, oversized reports, and inconsistent power limits.
- active-input countdown, exact idle-threshold, locked-session, and invalid-policy cases.

The UI deliberately has no automatic action, settings persistence, tray icon, startup entry, telemetry, or activity history in this milestone.
