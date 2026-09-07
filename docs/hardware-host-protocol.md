# Hardware host command protocol

Milestone 0.6 defines and tests the future elevated-helper command boundary while continuing to use only in-memory hardware.

This milestone does not yet launch an elevated process or implement inter-process transport. It establishes the messages and host-side validation that a later named-pipe transport must carry unchanged.

## Narrow command surface

A client command contains only:

- protocol and session IDs;
- a unique command ID and monotonic sequence number;
- UTC issue and expiry times;
- `ActivatePrepared` or `RestorePending`;
- a known recovery snapshot ID.

It cannot contain an arbitrary executable, DLL, native function, journal path, device path, device identifier, or power value. The host resolves the journal, controller, fingerprint, original limit, and target from its own trusted registry.

## Session authentication

- Each session receives a random 256-bit key.
- Commands carry an HMAC-SHA-256 tag over a canonical payload.
- Tags are compared in constant time.
- Default command lifetime is 30 seconds and cannot exceed two minutes.
- Session lifetime defaults to ten minutes and cannot exceed thirty minutes.
- Commands must arrive in exact sequence.
- Consumed command IDs cannot be replayed.
- Session keys are cleared from managed byte buffers when disposed.

A valid command is consumed before target lookup. This means a valid request for an unknown target cannot later be replayed if the target appears.

Authentication does not replace operating-system access control. The future transport must additionally restrict its named pipe to the expected local user/session and verify the peer before sharing session credentials.

## Run the protocol simulation

```powershell
dotnet run --project src/EcoPause.Simulation -- --scenario helper-protocol
```

The simulation demonstrates authenticated activation, replay rejection, tamper rejection, and authenticated restoration against an in-memory GPU.
