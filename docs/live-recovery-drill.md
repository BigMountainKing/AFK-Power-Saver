# Live elevated recovery drill

Milestone 1.7 verifies that EcoPause can recover a real NVIDIA power limit after the elevated process that applied it no longer exists. This remains a manual console drill. Later milestones add a separate qualified desktop button and hotkey; the idle timer remains preview-only.

## What the drill does

The first elevated process:

1. requires exactly one validated NVIDIA GPU at the fixed 450 W baseline;
2. writes the protected `Prepared` journal;
3. applies exactly 400 W and reads back exactly 400 W;
4. persists and reloads `Applied`;
5. sends the verified evidence;
6. intentionally exits with code 91 without restoring.

The normal-user launcher then queries the separate read-only provider and requires the same GPU at 400 W. It requests a second UAC approval and starts a fresh copy of the helper. Because the journal is pending, that process can select only `RecoveryOnly`: it restores and reads back exactly 450 W, persists `Restored`, deletes the one known journal and empty protected directory non-recursively, and exits normally. The launcher independently reads 450 W once more.

No phase, operation, GPU index, device identifier, path, watt value, or native function is supplied to the elevated helper. Journal presence and stage choose the phase. A pending journal always forces recovery.

## Before running

- Save open work.
- Close EcoPause, games, renderers, AI workloads, benchmarks, overclocking software, and GPU tuning tools.
- Keep local physical access to the PC.
- Do not run the Visual Studio terminal as administrator.
- Keep the administrator rollback command available.

Run the read-only preflight:

```powershell
dotnet run --project src/EcoPause.LiveRecoveryDrill --configuration Release -- --preflight
```

It must report one RTX 4090, current/default 450 W, permitted range 150–600 W, and `READY FOR TWO-PHASE DRILL`. If it reports possible pending recovery at 400 W, the live command will recover first and will not begin a new interruption.

## Independent rollback

From a separate administrator Windows Terminal if recovery cannot complete:

```powershell
nvidia-smi --query-gpu=index,name,power.limit,power.default_limit --format=csv
nvidia-smi --power-limit=450
nvidia-smi --query-gpu=index,name,power.limit --format=csv
```

The final query must show 450 W. Reboot Windows if the NVIDIA tool cannot restore the limit.

## Run the drill

From the normal Visual Studio terminal:

```powershell
dotnet run --project src/EcoPause.LiveRecoveryDrill --configuration Release -- --run-live-recovery-drill
```

Type exactly:

```text
INTERRUPT AT 400 W THEN RECOVER 450 W
```

Approve UAC prompt 1. After the launcher verifies intentional termination and 400 W independently, approve UAC prompt 2 immediately.

Successful highlights are:

```text
Helper 1 selected phase: CrashAfterVerifiedTarget
Authenticated operation: Executed / Activated
Helper exit: 91
Final phase limit: 400 W
Recovery journal stage: Applied
Intentional helper termination (91): PASS
Independent post-termination 400 W read-back: PASS
Pending recovery now exists outside the terminated elevated process.
Helper 2 selected phase: RecoveryOnly
Authenticated operation: Executed / Restored
Helper exit: 0
Final phase limit: 450 W
Recovery journal stage: Restored
450 W exact restoration: PASS
Protected recovery artifacts cleaned: True
Independent final live limit: 450 W
Live elevated recovery drill: PASS
```

## If prompt 2 is declined or the launcher closes

The 400 W state is a lower power ceiling, but recovery remains pending. Do not start a GPU workload. Run the same drill command again and approve UAC; the first helper will select `RecoveryOnly` and will not apply another target. If that cannot complete, use the independent rollback above and then reboot if necessary.

## Still excluded

- persistent live pause as a user feature;
- desktop integration was excluded from milestone 1.7 and was added later as the separately gated 1.9 manual action;
- hotkey or idle activation of real hardware;
- selectable live wattage;
- multi-GPU selection;
- background services, startup entries, scheduled tasks, or installer integration;
- clocks, voltage, fans, display state, or telemetry.
