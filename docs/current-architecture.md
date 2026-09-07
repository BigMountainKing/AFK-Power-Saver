# Current architecture — 1.25.0

This is the current behaviour reference. The milestone documents describe historical qualification steps.

## Profile ownership and recovery

The normal-user desktop uses `ProfileCoordinator` to own GPU, CPU, and display resources independently. All seven nonempty feature combinations use the same sequencing: GPU, CPU, then dimming. Failed activation restores any possibly changed resource. Recovery removes dimming, restores CPU, and restores GPU independently, retaining failed ownership for a retry. A CPU failure cannot prevent GPU restoration. Even a CPU-only activation that needs no power-plan write remains an active user profile until restored.

GPU probing is observational: refreshing GPU readings never clears CPU ownership or dismisses a local display profile. A hardware reading at default is not proof that a recovery journal is closed. The active-profile button remains a recovery action until owned resources are restored.

The idle timer claims only its own profiles. Activation is attempted once per unattended transition. Restoration retries with 2, 4, 8, and 16 second delays, for a maximum of five attempts. Ownership remains pending if the budget is exhausted; the window is shown with an actionable error. A manual recovery attempt resets the retry budget.

## GPU boundary

The desktop is normal-user. A parent-bound elevated broker and one-shot helper own GPU writes. Existing process-ID verification, protected journal ACLs, target validation, hardware mutex, and authenticated activation commands remain.

Startup, shutdown, and explicit restoration use a recovery-only helper behaviour. It consults the canonical protected journal even when the GPU is already at default. With no journal it performs no write. With a pending journal it verifies the device/constraints, restores if necessary, verifies the final value, and closes recovery. A closed journal is also reverified before cleanup. This behaviour cannot start a new activation. The command accepts no journal path, GPU identity, or restoration value from the caller.

Known `.recovery.json.<32-digit-guid>.tmp` files left by interrupted atomic writes are removed under the protected transaction lock. They are never promoted to authoritative snapshots. Unknown artifacts, directories and links remain rejected. The canonical journal is validated separately. Before the first canonical Prepared commit, the activation engine is not permitted to write hardware.

Production UI outcomes use bounded JSON with `GpuOperationResult`, including state, verified status, recovery ownership, and observed/default values. Broker results derive from verified helper evidence and independent read-back; human-readable transcripts remain diagnostics and historical qualification evidence. A disconnected/invalid session is not marked ready or reported as successfully restored.

## CPU companion

`AFKPowerSaver.CpuRecovery.exe` is a normal-user companion. Before CPU application the desktop starts it with the parent's PID, start time, and a random session owner ID. The worker opens the parent process handle and acknowledges readiness over inherited anonymous pipes. It waits for parent termination or stdin disconnection and then attempts recovery, with bounded retries.

The companion uses only the fixed per-user CPU journal path. A cross-process mutex serializes CPU transactions. Version 2 journals add an owner ID; the companion restores only a matching owner, so an old worker cannot restore a later desktop session's limits. Startup explicitly accepts and recovers version 1 journals too. Journal data is flushed to disk before atomic replacement and before power-plan writes. Failed recovery retains the journal for another attempt at startup. The separate CPU qualification tool shares this journal format and mutex.

CPU recovery restores exact AC/DC values in the modified scheme. It refreshes that scheme only when it is still active, preserving a different plan the user selected in the meantime. As with other Windows power-plan tools, competing external writes cannot be made fully atomic with these APIs.

A forcibly terminated companion or a persistent Windows API/storage failure can still leave recovery pending. The durable journal and next-startup recovery remain necessary. The worker is not a service and cannot recover while Windows is shut down.

## AMD status

AMD remains experimental and unverified on physical Radeon hardware. A successful default query is mandatory. Missing `IADLXManualPowerTuning1`, a failed `GetPowerLimitDefault`, an out-of-range default, or an invalid driver step disables control; zero is never substituted as a verified default. Five native tests exercise the actual selection code using fake ADLX interfaces, without loading a driver. The read-only GPU card labels AMD support experimental.

## Validation and release

Run `./packaging/Test-Safety.ps1` for native contract tests, the Release solution build, and all three non-mutating managed suites. Lifecycle tests cover feature combinations, partial rollback, independent recovery, retry timing, interrupted journal replacements, already-restored GPU recovery, legacy CPU journals, power-plan selection, and recovery-worker survival after actual parent process termination using fake restoration.

The Windows GitHub Actions workflow runs the same script on pushes and pull requests. It has read-only repository permissions and does not run live canaries or publish artifacts.

`./packaging/Build-Release.ps1` first runs the safety script, then publishes all five production executables, checks the assembled payload, runs the CPU companion's hardware-free self-test and desktop smoke modes, and verifies an isolated install/uninstall round trip. No production installation, power setting, or startup registration is changed by these test modes. Real GPU and CPU qualification remains an explicit operator procedure.
