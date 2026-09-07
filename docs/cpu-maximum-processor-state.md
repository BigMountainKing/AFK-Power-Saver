# CPU maximum processor state qualification

AFK Power Saver 1.16 introduces an isolated Windows power-plan qualification tool. It is not yet connected to the desktop app, hotkey, GPU limit, or idle timer.

## What the setting means

Windows **Maximum processor state** is a whole-PC performance-state ceiling expressed as a percentage. It applies to all CPU workloads using the active power plan. It is not a literal CPU-utilization limit, an exact clock speed, or a promise of proportional wall-power savings.

The tool reads and preserves both values:

- AC: the setting used while plugged in.
- DC: the setting used while running on battery.

## Read-only preflight

Run without arguments:

```powershell
& '.\AFK Power Saver CPU Preflight.exe'
```

This reads the active scheme and its exact AC/DC values. It does not call a power-plan write API.

## Five-second live canary

Close important CPU-heavy work first, then run:

```powershell
& '.\AFK Power Saver CPU Preflight.exe' --run-live-cpu-canary
```

The canary requires the exact phrase shown on screen. It writes a recovery journal before changing anything, applies 80% to AC and DC for five seconds, reads both values back, restores the exact original pair, verifies restoration, and removes the journal.

If the process is interrupted after the journal is written, run the same command again. A pending journal forces recovery before a new canary can start.

Independent inspection and rollback are available in an Administrator terminal:

```powershell
powercfg /query SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX
powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 100
powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 100
powercfg /setactive SCHEME_CURRENT
```

The rollback example sets both values to 100%. If your original values were different, use the exact values printed by the preflight instead.
