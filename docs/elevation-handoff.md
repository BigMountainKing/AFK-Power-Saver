# UAC elevation handoff

Milestone 0.8 verifies how EcoPause can isolate future privileged work in a small helper process. It deliberately performs the full exercise with an in-memory power controller.

## Security flow

1. A normal-user launcher creates a random current-user-only named-pipe server.
2. It starts `EcoPause.ElevatedHost.Simulation.exe` using the Windows `runas` verb.
3. The helper's embedded manifest requests administrator privileges, so Windows displays UAC consent.
4. The launcher asks Windows for the connected client process ID and requires it to equal the exact helper process it launched.
5. The helper asks Windows for the server process ID and requires it to equal the parent process ID supplied at launch.
6. Only after mutual verification does the helper create and send a short-lived authenticated session bootstrap.
7. Activation, replay, tampering, and restoration are exercised. Success requires exact restoration to 450 W in the fake controller and a closed recovery journal.

The helper chooses its own temporary recovery path. The launcher cannot send a path, device identifier, power value, native function name, or arbitrary operation across the elevated boundary.

The normal-user server retains `PipeOptions.CurrentUserOnly`. The elevated client does not use the client-side owner comparison because Windows token ownership can differ across integrity levels; instead, it must verify the exact server process ID before any session material is exchanged. This cross-integrity path was verified successfully on Windows during milestone 0.8.

## Run it

Open a normal, non-administrator terminal in Visual Studio and run:

```powershell
dotnet run --project src/EcoPause.Elevation.Simulation
```

Windows should display a UAC prompt for the unsigned local helper. Choose **Yes** to exercise the complete handoff. Choosing **No** is also safe and tests cancellation before connection.

Expected successful highlights:

```text
Mutual process-ID verification: PASS
Authenticated activation: Executed / Activated
Replay attempt: RejectedReplay
Tampered command: RejectedAuthentication
Authenticated restoration: Executed / Restored
Final simulated limit: 450 W
Recovery journal closed: True
Safe elevated lifecycle: PASS
Real NVIDIA access: NONE
```

If a hosted or sandboxed terminal reports status `0xc0000142` without displaying UAC, use Visual Studio's ordinary terminal. The host blocked creation of an elevated desktop process; no helper command ran.

## What remains excluded

- No NVIDIA setting function is imported.
- No real GPU write is attempted.
- No power value is supplied by the normal-user process.
- No background service is installed.
- No startup entry, scheduled task, driver, or persistent administrator component is created.
