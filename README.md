# AFK Power Saver

AFK Power Saver is an experimental Windows utility for safely reducing hardware power use while a gaming PC is idle or temporarily unattended.

The current milestones are:

- **EcoPause Hardware Probe 0.1**: a deliberately read-only console application. It validates that NVIDIA NVML can be loaded, detects installed NVIDIA GPUs, and reads the current/default/minimum/maximum power limits. It does not load or expose any NVML setting functions.
- **Recovery Journal 0.2**: an immutable recovery state machine with atomic, integrity-checked persistence. It validates the future `prepare -> apply -> restore` sequence but still performs no hardware writes.
- **Dry-Run Activation 0.3**: connects live GPU readings to a persisted recovery-shaped plan. Dry-run files are explicitly inactive and cannot transition hardware state.
- **Simulated Activation 0.4**: runs the complete write, verify, and rollback sequence against an in-memory GPU, including failure and interruption scenarios.
- **Startup Recovery 0.5**: discovers pending journals and requires a short-lived, one-use approval bound to the exact snapshot before simulated restoration.
- **Hardware Host Protocol 0.6**: authenticates minimal one-use helper commands and rejects tampering, replay, expiry, sequence gaps, and unknown targets against in-memory hardware.
- **Named-Pipe Transport 0.7**: carries those commands between separate normal-user Windows processes over a current-user-only pipe with bounded frames and timeouts. Hardware remains in-memory.
- **Elevation Handoff 0.8**: launches a manifest-declared administrator helper through UAC, mutually verifies the launcher and helper process IDs through Windows, and exercises the authenticated lifecycle against fake hardware only.
- **Elevated Crash Recovery 0.9**: persists a fake 400 W state and an applied recovery journal, terminates the first elevated helper intentionally, then launches a fresh verified helper that restores exactly 450 W and cleans the protected simulation state.
- **Desktop Safety Preview 1.0**: adds a normal-user WPF interface that streams the verified fake lifecycle and shows success only when the complete authentication, rejection, restoration, and no-NVIDIA evidence is present.
- **Live Read-Only Desktop Data 1.1**: displays sanitized NVIDIA model, power-limit, live draw, and permitted-range data in the WPF interface through a bounded read-only child probe. The lifecycle button still operates on fake hardware only.
- **Windows Activity Preview 1.2**: observes read-only keyboard/mouse idle duration and session lock/unlock events, then previews when a five-minute policy would pause. Automatic action remains explicitly off.
- **Global Hotkey Activation 1.3**: registers `Ctrl+Shift+F3` as a no-repeat Windows hotkey and routes it through the same guarded fake lifecycle as the button. `Alt+F3` is deliberately avoided because NVIDIA uses it for Game Filter.
- **Persistent Fake Toggle 1.4**: makes the hotkey stateful against protected fake hardware. The first press leaves a recovery-pending 400 W fake pause; the second restores exactly 450 W and cleans the protected artifacts, including after a desktop restart.
- **Configurable Fake Targets 1.5**: adds allow-listed 250, 300, 350, and 400 W simulation profiles. The UI validates the selection against live read-only driver constraints, while the helper receives only a fixed profile identity and always restores any pending profile first.
- **Supervised Live GPU Canary 1.6**: adds a separate manual-only console path for one tightly constrained real NVIDIA test. After a read-only preflight and exact typed confirmation, the elevated helper either recovers a protected pending journal or applies exactly 400 W for five seconds, verifies it, restores exactly 450 W, verifies restoration, and removes its journal. The desktop, hotkey, and idle timer remain fake-only.
- **Live Elevated Recovery Drill 1.7**: uses a separate manual-only launcher and journal-selected helper. The first elevated process applies and verifies 400 W, persists `Applied`, and intentionally exits with code 91 without restoration. The normal-user launcher independently confirms 400 W, then a fresh elevated process discovers the protected journal, restores and verifies 450 W, and cleans the artifacts. A pending journal always forces recovery instead of another interruption.
- **Manual Persistent Live Toggle 1.8**: extends the qualified recovery launcher/helper with one fixed behavior choice that cannot carry wattage, device, path, or operation data. One supervised invocation applies and verifies 400 W and deliberately retains the protected exact-restoration journal; the next invocation restores and verifies 450 W and cleans it. Any pending journal always forces recovery, and desktop, hotkey, and idle actions remain fake-only.
- **Gated Desktop Live Toggle 1.9**: adds a separate amber WPF action that is enabled only after strict read-only validation of one NVIDIA GPU matching the fixed 400/450 W contract. It requires an explicit state-specific warning dialog and UAC, starts the qualified normal-user launcher as a bounded child process, and accepts success only from a complete state-specific transcript followed by a fresh read-only GPU refresh. The green button, `Ctrl+Shift+F3`, and idle timer remain fake-only.
- **Selectable Live Targets 1.10**: adds a separate amber dropdown for six compile-time live profiles: 150, 200, 250, 300, 350, and 400 W. The desktop passes only a profile ID; the elevated helper resolves the fixed wattage itself, verifies it against the driver range, and always restores the exact pending journal profile before considering a newly selected target.
- **Confirmed Live Hotkey 1.11**: routes `Ctrl+Shift+F3` through the same target-specific warning, UAC, recovery-first helper, strict transcript evidence, and fresh read-only verification as the amber live button. The green button remains fake-only and the idle observer remains preview-only.
- **Startup-Authorized Live Session 1.12**: starts one mutually process-ID-verified elevated broker after a single UAC prompt when the normal-user desktop opens. The amber button and `Ctrl+Shift+F3` then toggle immediately without another dialog or prompt. Each request is still a fixed profile ID, each action uses the existing recovery-first one-shot helper, and orderly app shutdown restores 450 W before the broker exits.
- **Portable Controls 1.13**: lets the user assign a persisted global hotkey and replaces fixed live watt choices with a 1%-step slider. The slider is clamped to the detected GPU's own minimum and 99% of its verified default, displays the resolved wattage, passes only a bounded `percent-N` identity, and restores the exact device default. The fixed 400/450 W canary and crash drill remain separate qualification tools.
- **AFK Power Saver 1.14**: adopts the product name and “limit” terminology and fixes dark-theme dropdown popup colours.
- **CPU Power-Plan Qualification 1.16**: retires the launch-only FPS experiment and adds an isolated Windows Maximum processor state preflight and five-second canary. The canary journals the exact active-plan AC/DC values, applies 80% globally, verifies it, and restores the exact original pair. It is not yet connected to the desktop controls.
- **Screen Overlay Qualification 1.17**: adds an isolated multi-monitor black overlay with adjustable 10-95% opacity. It verifies Windows click-through/no-focus styles, registers `Ctrl+Shift+F12` as an emergency dismissal, and always auto-dismisses after 15 seconds. It does not change hardware settings and is not yet connected to the desktop limit toggle.
- **Native Overlay Correction 1.17.1**: replaces the initial WPF dimming surfaces with native layered windows after live testing exposed unreliable opacity and input routing. Native alpha and extended safety styles are now read back exactly, hit testing resolves to the underlying window, and topmost order is refreshed during the canary.
- **Integrated Screen Dimming 1.18.1**: moves the qualified native overlay into a shared Windows library and connects it to the startup-authorized live GPU lifecycle. Users can enable dimming, select 10-95% opacity, and target All monitors or one detected display. Limit creates the overlay only after verified GPU limiting; restore and shutdown remove it before GPU restoration. Saved disconnected displays fall back safely to All monitors. The 1.18.1 package also includes the complete managed payload for every companion executable used by GPU probing and startup elevation.
- **Optional Integrated CPU Limit 1.19**: adds a production checkbox and 20-99% Maximum processor state control to the same button/hotkey lifecycle. CPU limiting defaults off. When enabled, AFK Power Saver records the exact original AC/DC values before applying a ceiling that can never raise either existing value. Toggle-off, restore, shutdown, and the next startup after interruption all prioritize exact recovery. The obsolete fake-GPU button and selector are removed from the production UI and release package; simulation projects remain development-only regression coverage.
- **CPU UI-Thread Correction 1.19.1**: captures the selected CPU percentage on the WPF UI thread before starting the background power-plan operation. The initial 1.19 package safely rejected the cross-thread access and rolled the already-limited GPU back exactly, but never reached the CPU write.
- **Dark Menus and Clean Exit 1.19.2**: replaces the Windows-theme-dependent combo boxes with an explicit dark selector and popup template, keeping both selected values and every dropdown option readable. Closing with the title-bar X or `Alt+F4` now completes overlay, CPU, GPU broker, hotkey, and activity cleanup, closes the last window, explicitly shuts down WPF, and releases the single-instance lock.
- **Experimental AMD Radeon Support 1.24**: adds automatic AMD detection after the NVIDIA probe, then uses AMD ADLX manual power tuning through a small isolated native bridge. Radeon limits are expressed as the percentage adjustment reported by the driver, restricted to below the factory value, verified after each write, and restored from a separate protected journal. This path is contract-tested but remains unverified on physical Radeon hardware.

## Safety boundary

- The normal-user NVIDIA/AMD probe remains read-only. The desktop contains no GPU write API; its button, assigned global hotkey, or enabled idle timer can request only a validated percentage through the startup-authorized launcher.
- CPU Maximum processor state is a separate normal-user Windows power-plan boundary. It is optional, journals exact AC/DC values before writes, and never raises an existing lower value.
- The desktop's startup-authorized broker and the explicit NVIDIA qualification tools are the only paths that can start an administrator GPU helper.
- NVIDIA targets resolve to a whole-watt value derived from the validated GPU default. AMD targets use the ADLX-reported manual power adjustment and driver step. Both paths accept only below-default settings and restore the exact journaled original value.
- GPU clocks, voltage, fans, arbitrary watt values, and ambiguous multi-GPU control remain excluded. Screen dimming is a click-through overlay and does not change display hardware state.
- It does not collect GPU UUIDs, serial numbers, usernames, or telemetry.
- Unsupported capabilities are reported rather than approximated.

All hardware-changing milestones follow: **read -> validate -> journal -> apply -> verify -> restore -> verify**.

## Requirements

- Windows 11 x64
- Visual Studio 2026 with **.NET desktop development** and **Desktop development with C++** (for building the AMD bridge)
- .NET 10 SDK
- An NVIDIA display driver for NVML control, or a current AMD Radeon display driver providing ADLX

## Build and run

From the repository root:

```powershell
dotnet build EcoPause.sln
dotnet run --project src/EcoPause.Probe
```

For machine-readable output that still omits unique hardware identifiers:

```powershell
dotnet run --project src/EcoPause.Probe -- --json
```

Create and verify a 400 W dry-run plan without changing hardware:

```powershell
dotnet run --project src/EcoPause.Probe -- --dry-run-watts 400
```

Run the complete fake-hardware activation suite:

```powershell
dotnet run --project src/EcoPause.Simulation
```

Run only startup discovery and approval:

```powershell
dotnet run --project src/EcoPause.Simulation -- --scenario startup-recovery
```

Run the future elevated-helper protocol simulation:

```powershell
dotnet run --project src/EcoPause.Simulation -- --scenario helper-protocol
```

Run the separate-process named-pipe safety simulation:

```powershell
dotnet run --project src/EcoPause.Transport.Simulation
```

Run this command from a normal terminal. The simulation intentionally refuses to run as administrator.

Run the UAC handoff simulation from a normal Visual Studio terminal:

```powershell
dotnet run --project src/EcoPause.Elevation.Simulation
```

Windows will ask permission to run the unsigned local simulation helper. This phase still uses fake hardware and does not reference the NVIDIA provider.

Run the elevated crash-recovery simulation from a normal Visual Studio terminal:

```powershell
dotnet run --project src/EcoPause.CrashRecovery.Simulation
```

This simulation shows two UAC prompts: one for the helper that terminates after fake activation, and one for the fresh recovery helper. If the second prompt is declined, the launcher prints a `--recover-run` command that resumes the pending fake recovery later.

Open the desktop safety preview:

```powershell
dotnet run --project src/EcoPause.Desktop
```

The desktop app refuses elevated startup. It obtains GPU measurements from the separate read-only probe through a strict, bounded JSON contract. Its optional idle automation accepts a user-selected one-to-120-minute delay, uses read-only idle and lock signals, and enters the same startup-authorized live path as the single activation button and assigned hotkey. Idle automation defaults off. A timer-applied profile restores when activity resumes; manually applied profiles remain under manual control.

The percentage slider and live controls are enabled only for one unambiguous supported NVIDIA or AMD GPU at its verified default or a recoverable percentage target. NVIDIA is preferred when both providers are available. One startup UAC prompt authorizes a parent-bound elevated broker. The protected helper still chooses limit or recovery from its provider-specific journal for every button, hotkey, or enabled idle-timer request. The WPF process contains no hardware controller and requires a strict percentage-aware transcript plus fresh read-only GPU refresh before showing success. See [the desktop live-session procedure](docs/desktop-manual-live-toggle.md).

Run the live canary's read-only preflight from a normal Visual Studio terminal:

```powershell
dotnet run --project src/EcoPause.LiveCanary --configuration Release -- --preflight
```

Only after following [the supervised operator procedure](docs/live-gpu-canary.md), run the fixed live canary:

```powershell
dotnet run --project src/EcoPause.LiveCanary --configuration Release -- --run-live-canary
```

The live command requires an exact typed confirmation and one UAC approval. Neither the command line nor the normal-user process can choose a GPU, path, watt value, hold time, or operation. If a protected journal is pending, the helper restores it instead of beginning a new canary.

Run the recovery drill's read-only preflight:

```powershell
dotnet run --project src/EcoPause.LiveRecoveryDrill --configuration Release -- --preflight
```

Only after following [the live recovery drill procedure](docs/live-recovery-drill.md), run:

```powershell
dotnet run --project src/EcoPause.LiveRecoveryDrill --configuration Release -- --run-live-recovery-drill
```

A new drill normally shows two UAC prompts. If a prior protected journal is already pending, the first helper selects recovery and the launcher does not begin another interruption.

Run the manual persistent toggle's read-only preflight:

```powershell
dotnet run --project src/EcoPause.LiveRecoveryDrill --configuration Release -- --preflight-live-toggle
```

Only after following [the manual persistent live toggle procedure](docs/manual-persistent-live-toggle.md), run one fixed toggle operation:

```powershell
dotnet run --project src/EcoPause.LiveRecoveryDrill --configuration Release -- --run-live-toggle
```

The first successful invocation normally leaves the GPU at 400 W with protected recovery pending. Run the same command again to restore exactly 450 W and clean the journal. Each invocation requires exact typed confirmation and one UAC approval. The helper selects pause or recovery from protected state; the launcher cannot choose the operation.

Run the CPU power-plan read-only preflight:

```powershell
dotnet run --project src/AFKPowerSaver.CpuCanary --configuration Release
```

Only after following [the CPU maximum processor state procedure](docs/cpu-maximum-processor-state.md), run the five-second 80% canary:

```powershell
dotnet run --project src/AFKPowerSaver.CpuCanary --configuration Release -- --run-live-cpu-canary
```

This changes both AC and DC Maximum processor state values on the active Windows power plan, verifies the temporary values, then restores the exact original pair. It is a global performance-state ceiling, not a literal utilization or exact clock limit.

Run the isolated screen overlay test:

```powershell
dotnet run --project src/AFKPowerSaver.OverlayCanary --configuration Release
```

Follow [the screen overlay canary procedure](docs/screen-overlay-canary.md). The test uses `Ctrl+Shift+F12` as a global emergency exit and also dismisses every overlay automatically after 15 seconds.

The integrated main-app screen dimming controls are described in [the desktop screen-dimming procedure](docs/desktop-screen-dimming.md). They are optional and use the primary activation button, assigned hotkey, or enabled idle timer; no second hotkey or UAC prompt is introduced.

Run the dependency-free safety tests:

```powershell
dotnet run --project tests/EcoPause.Hardware.Tests
dotnet run --project tests/EcoPause.Core.Tests
```

## Solution layout

```text
EcoPause.sln
src/
  EcoPause.Hardware.Abstractions/  Shared immutable probe models and interfaces
  EcoPause.Hardware.Nvidia/        Dynamically loaded, read-only NVML queries
  EcoPause.Hardware.Nvidia.Control/ Setter isolated to the elevated fixed canary
  EcoPause.Hardware.Amd/           ADLX probe and isolated live-control adapter
  EcoPause.HardwareHost.Protocol/  Minimal authenticated helper command boundary
  EcoPause.HardwareHost.Transport/ Current-user-only named pipe and bounded framing
  EcoPause.ElevatedHost.Simulation/ Manifest-elevated fake hardware host
  EcoPause.CrashRecovery.SimulationModel/ Persistent fake state and fixed recovery profile
  EcoPause.Desktop.Safety/          Strict transcript and read-only probe data validation
  EcoPause.Core/                   Recovery model, journal, and dry-run coordinator
  EcoPause.Probe/                  Console diagnostic application
  EcoPause.Simulation/             In-memory activation and rollback demonstration
  EcoPause.Transport.Simulation/   Separate-process transport demonstration
  EcoPause.Elevation.Simulation/   Normal-user UAC launcher and peer verification
  EcoPause.CrashRecovery.Simulation/ Two-helper elevated crash-recovery demonstration
  EcoPause.Desktop/                 Normal-user WPF safety preview
  EcoPause.LiveCanary.Model/        Fixed canary policy and bounded wire records
  EcoPause.ElevatedHost.LiveCanary/ Manifest-elevated fixed NVIDIA canary helper
  EcoPause.LiveCanary/              Normal-user preflight and supervised launcher
  EcoPause.ElevatedHost.LiveRecoveryDrill/ Journal-selected drill/persistent-toggle helper
  EcoPause.LiveRecoveryDrill/       Normal-user recovery drill and manual live toggle
  AFKPowerSaver.PowerPlan/          Windows processor-state read/write boundary
  AFKPowerSaver.CpuCanary/          Read-only CPU preflight and supervised canary
  AFKPowerSaver.OverlayModel/       Bounded opacity and timeout policy
  AFKPowerSaver.Overlay.Windows/     Shared native display catalog and click-through overlays
  AFKPowerSaver.OverlayCanary/      Multi-monitor click-through overlay test
tests/
  EcoPause.Hardware.Tests/         Dependency-free domain and API safety tests
  EcoPause.Core.Tests/             Recovery transitions and corruption tests
```

## Current release candidate

Milestone 1.24.0 adds an experimental AMD Radeon backend using the ADLX component installed by the AMD display driver. Detection is automatic, NVIDIA remains preferred when available, and the existing button/hotkey/idle lifecycle is reused without a second control path. The AMD slider shows percent of the factory power setting because ADLX exposes an adjustment percentage rather than an absolute watt cap. Exact read-back and provider-specific protected recovery are required. The implementation is compiled and contract-tested, but must still be qualified on physical Radeon hardware before being treated as production-proven.

For public Authenticode releases, follow [the code-signing procedure](docs/code-signing.md). The release pipeline can require a certificate-store identity, sign and timestamp every shipped executable and library before packaging, sign the outer installer, and verify every resulting signature.
