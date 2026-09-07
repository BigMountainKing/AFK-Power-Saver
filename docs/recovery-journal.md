# Recovery journal design

Milestone 0.2 defines how EcoPause must preserve enough trusted local state to recover an exact original GPU power limit after a crash or interruption. It does not add hardware write operations.

## State sequence

1. Read the current limit and the driver-supported minimum and maximum.
2. Validate that the proposed target is inside the supported range and does not raise the current limit.
3. Persist a `Prepared` journal with the exact original and proposed target values.
4. In a future milestone, apply the target and read it back.
5. Only after an exact target read-back, persist `Applied`.
6. Restore the exact original limit and read it back.
7. Only after an exact original read-back, persist `Restored`.

Both `Prepared` and `Applied` are treated as recovery-pending. This is intentional: a process could terminate after a future write but before it updates the journal from `Prepared` to `Applied`.

This applies to journals whose purpose is `LiveRecovery`. Milestone 0.3 also uses the schema for `DryRunPlan` files. Those files are explicitly inactive, always remain `Prepared`, and never trigger recovery.

## Persistence guarantees

- Values are stored in integer milliwatts to avoid rounding.
- The journal is written to a unique temporary file in the destination directory, flushed to disk, and moved over the live file.
- A SHA-256 integrity value detects accidental modification or partial/corrupt content.
- The schema validates all fields again when loading.
- The proposed target must be within driver constraints and cannot exceed the original limit.
- Applied and restored stages require an exact observed value.
- The raw stable GPU identifier is hashed before persistence.

The integrity digest detects corruption; it is not an authorization mechanism. A future elevated helper must independently validate every request and must not trust writable user input merely because a digest matches.
