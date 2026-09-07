# Elevated crash-recovery simulation

Milestone 0.9 tests recovery after an elevated helper disappears while a change is pending. The “hardware” is a small integrity-checked file; the NVIDIA provider is not referenced.

The complete two-helper lifecycle has been verified on Windows: intentional exit code `91` was observed after activation, a fresh helper restored exactly 450 W, the journal closed, and the protected simulation files were cleaned.

## Expected sequence

1. The normal-user launcher creates a random run ID and a current-user-only pipe.
2. UAC starts the first elevated helper.
3. Both processes verify the exact connected peer PIDs.
4. The helper creates administrator-protected simulation state beneath `%ProgramData%\EcoPauseCrashRecoverySimulation\<run-id>`.
5. An authenticated activation persists `Prepared`, changes the fake limit from 450 W to 400 W, verifies it, and persists `Applied`.
6. The helper exits immediately with the intentional code `91`, without running disposal or restoration logic.
7. The launcher observes that termination and starts a completely new helper through a second UAC prompt.
8. The recovery helper validates the protected path, journal integrity, fixed profile, device fingerprint, `Applied` stage, and observed 400 W fake state.
9. An authenticated `RestorePending` command restores exactly 450 W, verifies it, closes the journal, and removes the two known simulation files.

The normal launcher supplies only a random run ID. The elevated helper canonicalizes it beneath its fixed root; paths and power limits never cross the pipe.

## Run it

Open a normal Visual Studio terminal and run:

```powershell
dotnet run --project src/EcoPause.CrashRecovery.Simulation
```

Approve both UAC prompts. The executable is an unsigned local build, so “Unknown publisher” is expected.

Expected highlights:

```text
Persisted fake activation: Executed / Activated
Intentional helper termination (91): PASS
Pending recovery now survives outside the terminated process.
Fresh-helper restoration: Executed / Restored
Final persistent fake limit: 450 W
Recovery journal closed: True
Protected simulation files cleaned: True
Elevated crash recovery: PASS
Real NVIDIA access: NONE
```

## Resume after declining recovery

If the second UAC prompt is declined or the launcher closes after the first helper terminates, retain the printed run ID and use:

```powershell
dotnet run --project src/EcoPause.CrashRecovery.Simulation -- --recover-run <32-character-run-id>
```

This starts only the fresh recovery helper. The pending files represent fake hardware and do not affect the GPU, but completing recovery verifies the intended lifecycle and removes them.

## Explicit exclusions

- No NVIDIA assembly or native GPU setter is referenced.
- No real hardware value is read or written.
- No service, scheduled task, startup entry, driver, or persistent administrator process is installed.
- Cleanup deletes only the two fixed simulation filenames and their now-empty canonical run directory; it does not recursively delete directories.
