# Simulated activation engine

Milestone 0.4 exercises the complete activation and restoration sequence against an in-memory GPU. The simulation executable does not reference the NVIDIA or AMD hardware projects.

## Activation sequence

1. Require a `Prepared` snapshot whose purpose is `LiveRecovery`.
2. Match the controller fingerprint to the snapshot.
3. Persist the prepared journal before the first write attempt.
4. Read the simulated current value and require an exact match with the recorded original.
5. Set the proposed target.
6. Read the target back exactly.
7. Persist the journal as `Applied` and recovery-pending.

## Failure handling

After any possible write, every ordinary failure enters rollback. Rollback uses an uncancelled token even if activation was cancelled, sets the exact original value, reads it back, and only then marks the journal `Restored`.

If either hardware restoration or journal persistence cannot be verified, the result is `FailedRollbackIncomplete`. It never claims a safe restore, and the last trustworthy journal remains recovery-pending.

## Run the simulations

From the solution root:

```powershell
dotnet run --project src/EcoPause.Simulation
```

This runs three scenarios:

- successful activation followed by restoration;
- incorrect target read-back followed by rollback;
- interruption immediately after a target write followed by rollback.

Run one scenario with:

```powershell
dotnet run --project src/EcoPause.Simulation -- --scenario interruption
```

All setting operations shown in the event trace modify only an in-memory integer.
