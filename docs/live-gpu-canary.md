# Supervised live GPU canary

Milestone 1.6 is EcoPause's first deliberately constrained real GPU write. It remains a separate manual console experiment. Later milestones add the qualified desktop live button and hotkey; the idle countdown remains preview-only.

## Fixed experiment

The elevated helper permits one lifecycle:

1. require exactly one NVIDIA GPU;
2. require a 450 W current limit and 450 W default limit, unless protected recovery is already pending at 400 W;
3. require live driver constraints containing both 400 W and 450 W;
4. persist an integrity-checked `Prepared` journal under `%ProgramData%\EcoPauseLiveCanary`;
5. apply exactly 400 W and read back exactly 400 W;
6. hold for five seconds;
7. restore exactly 450 W and read back exactly 450 W;
8. persist `Restored`, delete only `recovery.json`, and remove the empty directory non-recursively.

No GPU index, device identifier, path, watt value, hold duration, native function, or activation/recovery choice crosses from the launcher to the helper. If a valid `Prepared` or `Applied` journal exists, the helper chooses recovery and does not begin another canary.

## Before the live run

- Save open work.
- Close games, renderers, AI workloads, benchmarks, overclocking tools, and anything else doing important GPU work.
- Keep local physical access to the PC; do not perform the first run over a remote-only connection.
- Ensure Windows is not waiting for a restart or NVIDIA driver update.
- Do not run the terminal itself as administrator; EcoPause isolates elevation in the helper.

Run the read-only preflight:

```powershell
dotnet run --project src/EcoPause.LiveCanary --configuration Release -- --preflight
```

It must report one RTX 4090, current/default 450 W, range 150–600 W, target 400 W, and `Preflight state: READY`. This command cannot start UAC and uses the existing read-only provider.

## Independent rollback

Before the live experiment, know the independent recovery commands. Open a separate administrator Windows Terminal only if EcoPause fails to report exact restoration:

```powershell
nvidia-smi --query-gpu=index,name,power.limit,power.default_limit,power.min_limit,power.max_limit --format=csv
nvidia-smi --power-limit=450
nvidia-smi --query-gpu=index,name,power.limit --format=csv
```

The final query must show 450 W. If NVML or `nvidia-smi` cannot restore the value, reboot Windows; NVIDIA documents the software power limit as non-persistent across a reboot or driver unload.

## Run the supervised canary

From the normal Visual Studio terminal:

```powershell
dotnet run --project src/EcoPause.LiveCanary --configuration Release -- --run-live-canary
```

Read the warning, then type this exact phrase when requested:

```text
APPLY 400 W THEN RESTORE 450 W
```

Approve the single UAC prompt. A successful first run ends with:

```text
Helper-selected mode: Canary
Helper-selected fixed target: 400 W
400 W target applied and read back: PASS
450 W original restored and read back: PASS
Final live GPU limit: 450 W
Recovery journal stage: Restored
Protected recovery artifacts cleaned: True
Live GPU canary: PASS
Desktop live triggers: separate qualified toggle path
Idle timer real-GPU access: NONE
```

## If the run is interrupted

If the launcher or helper exits without the complete successful transcript:

1. do not start a game or GPU workload;
2. run the same `--run-live-canary` command again and approve recovery;
3. expect `Helper-selected mode: RecoveryOnly` and exact 450 W restoration;
4. if recovery cannot start or does not verify 450 W, use the independent administrator rollback above;
5. reboot if the NVIDIA tool cannot restore the limit.

A `Prepared` journal is intentionally treated as pending even if the visible limit is still 450 W. The process may have terminated after a native write but before it could record `Applied`; recovery must resolve that ambiguity before any new canary.

## Excluded from milestone 1.6

- persistent live power reduction;
- desktop integration was excluded from milestone 1.6 and was added later as a separate fixed manual action;
- hotkey access to the real controller;
- idle/session-lock activation;
- selectable live targets;
- multi-GPU selection;
- background service, startup entry, scheduled task, or installer;
- clocks, voltage, fans, display state, or telemetry.
