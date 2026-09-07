# Startup recovery and approval

Milestone 0.5 proves that a newly started EcoPause process can discover recovery state without automatically changing hardware.

## Discovery

The scanner examines only `*.json` files directly inside one configured recovery directory. It does not recurse and processes at most 128 files. Each file is integrity-checked and classified as:

- `PendingLiveRecovery`;
- `RestoredLiveRecovery`;
- `DryRunPlan`;
- `InvalidJournal`.

Dry-run, restored, and invalid files can never become recovery actions. Invalid content is reported but not trusted.

## Explicit approval contract

A pending live journal can produce a short-lived approval request. The request is bound to:

- a unique one-use request ID;
- the exact snapshot ID;
- the exact journal path;
- provider, stage, and original limit;
- a UTC creation and expiry time.

The application must display the generated confirmation summary and create either an approval or rejection decision. Rejection performs no controller operation. Expired, mismatched, or reused decisions are invalid.

Immediately before restoration, the engine reloads the journal and requires it to equal the snapshot that was approved. If the file changed after approval, recovery stops before reading or writing the controller.

## Run the startup simulation

```powershell
dotnet run --project src/EcoPause.Simulation -- --scenario startup-recovery
```

The simulation creates one pending live journal, one restored journal, one dry-run plan, and one corrupt file. It demonstrates a rejected decision with zero writes, followed by a separately approved exact restoration on an in-memory GPU.
