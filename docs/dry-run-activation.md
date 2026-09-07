# Dry-run activation coordinator

Milestone 0.3 connects live NVIDIA probe data to the recovery model without loading or calling hardware-setting functions.

The coordinator:

1. Runs the read-only GPU probe.
2. Selects the requested GPU by index.
3. Requires supported current, minimum, and maximum power-limit readings.
4. Requires a stable device identity, stored only as a SHA-256 fingerprint.
5. Validates that the proposed target is within the driver range and does not exceed the current limit.
6. Creates a recovery-shaped snapshot marked `DryRunPlan` and `Prepared`.
7. Atomically saves the plan and loads it back to verify the exact persisted content.

A dry-run plan has `isRecoveryPending: false`. This prevents a future startup-recovery scanner from treating a planning exercise as evidence that hardware was changed. Dry-run plans are also forbidden from transitioning to `Applied` or `Restored`.

## Run against the local GPU

From the solution root:

```powershell
dotnet run --project src/EcoPause.Probe -- --dry-run-watts 400
```

The default plan is written to `artifacts/dry-run/recovery-plan.json`. Choose another path or GPU with:

```powershell
dotnet run --project src/EcoPause.Probe -- --dry-run-watts 400 --gpu-index 0 --journal .\artifacts\dry-run\my-plan.json
```

This command does not require administrator privileges and does not change the GPU.
