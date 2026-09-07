# Named-pipe transport safety boundary

Milestone 0.7 verifies EcoPause's proposed process boundary without touching real hardware.

## What runs

`EcoPause.Transport.Simulation` starts two child processes under the same normal Windows user:

1. The host owns a file-backed recovery journal and an in-memory power controller initially set to 450 W.
2. The client receives a short-lived session bootstrap through a random current-user-only named pipe.
3. The client sends authenticated activation, replay, tampered restoration, and valid restoration messages.
4. The host resolves the snapshot ID from its own registry and never accepts a path, device identifier, wattage, or native function from the client.
5. The run succeeds only when replay and tampering are rejected and the controller finishes at exactly 450 W with a closed journal.

## Transport controls

- Generated pipe names have the exact form `EcoPause-` plus 32 hexadecimal characters.
- Both pipe ends request `PipeOptions.CurrentUserOnly`.
- One server instance is allowed.
- Frames are four-byte little-endian length-prefixed JSON.
- Empty frames and frames larger than 64 KiB are rejected before payload allocation or processing.
- JSON depth is limited to 16.
- I/O operations time out after five seconds; connections time out after ten seconds.
- Session and serialized frame buffers are cleared after use where managed memory permits.
- The executable refuses to run with an administrator token.

## Run it

From the `EcoPause` directory in a normal PowerShell or Visual Studio terminal:

```powershell
dotnet run --project src/EcoPause.Transport.Simulation
```

Expected highlights:

```text
Authenticated activation: Executed / Activated
Replay attempt: RejectedReplay
Tampered command: RejectedAuthentication
Authenticated restoration: Executed / Restored
Final simulated limit: 450 W
Safe final state: True
Separate-process transport: PASS
No NVIDIA code referenced: PASS
```

No administrator prompt should appear, and no real GPU setting is performed.
