# Manual persistent live toggle

Milestone 1.8 introduced a supervised console experiment that can leave the one validated NVIDIA GPU at a fixed 400 W ceiling until the operator runs the same command again. Milestone 1.9 reuses this qualified path from a separately confirmed WPF button, and milestone 1.11 allows `Ctrl+Shift+F3` to enter that same confirmed desktop path. The idle timer remains preview-only.

## Fixed safety contract

- exactly one NVIDIA GPU;
- current/default baseline of exactly 450 W, or recoverable 400 W;
- fixed target of exactly 400 W;
- protected journal at `%ProgramData%\EcoPauseLiveCanary\recovery.json`;
- one exact typed confirmation and one UAC prompt per invocation;
- authenticated, one-use helper command after mutual process-ID verification;
- helper-selected operation from the protected journal;
- independent normal-user read-back after the helper exits;
- no selectable device, path, wattage, duration, clock, voltage, fan, or display operation.

The fixed behavior token only distinguishes the deliberate interruption drill from an orderly persistent pause. It cannot choose the operation. Any `Prepared` or `Applied` journal forces `RecoveryOnly` and exact 450 W restoration.

## Before each invocation

Close games, renderers, AI workloads, benchmarks, GPU tuning utilities, and other important GPU work. Keep this independent administrator fallback available:

```powershell
nvidia-smi --power-limit=450
```

A reboot or NVIDIA driver reload is the final fallback. If an independent rollback or reboot restores 450 W while the journal remains, run EcoPause again so its recovery-only path verifies 450 W and cleans the protected state.

## Read-only preflight

From a normal Visual Studio terminal:

```powershell
dotnet run --project src/EcoPause.LiveRecoveryDrill --configuration Release -- --preflight-live-toggle
```

At 450 W it should report `READY TO PAUSE`. At 400 W it should report `RECOVERY EXPECTED`. Preflight never starts UAC or changes hardware.

## Pause at 400 W

Run:

```powershell
dotnet run --project src/EcoPause.LiveRecoveryDrill --configuration Release -- --run-live-toggle
```

Type exactly:

```text
TOGGLE LIVE GPU BETWEEN 400 W AND 450 W
```

Approve the UAC prompt. A successful new pause includes:

```text
Helper-selected phase: PauseAtVerifiedTarget
Authenticated operation: Executed / Activated
Helper exit: 0
Final helper limit: 400 W
Recovery journal stage: Applied
400 W exact persistent pause: PASS
Protected recovery artifacts retained: PASS
Independent final live limit: 400 W
Manual persistent live toggle: PASS / Paused
```

The lower ceiling remains active after the console exits. Do not delete or modify the protected journal.

## Restore to 450 W

Run the same command again, type the same exact phrase, and approve UAC. A successful restoration includes:

```text
Helper-selected phase: RecoveryOnly
Authenticated operation: Executed / Restored
Helper exit: 0
Final helper limit: 450 W
Recovery journal stage: Restored
450 W exact restoration: PASS
Protected recovery artifacts cleaned: True
Independent final live limit: 450 W
Manual persistent live toggle: PASS / Restored
```

If any output is incomplete, run the same command again to prioritize recovery. If EcoPause cannot recover, use the independent administrator rollback and reboot fallback above.

## Still excluded

- real-GPU idle activation;
- selectable live wattage;
- multi-GPU selection;
- background services, scheduled tasks, startup entries, or installer integration;
- clocks, voltage, fans, displays, or telemetry.
