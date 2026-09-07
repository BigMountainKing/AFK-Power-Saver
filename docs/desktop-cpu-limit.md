# Optional desktop CPU limit

AFK Power Saver 1.19.1 integrates Windows Maximum processor state with the existing live limit/restore action. Version 1.19.1 also captures the selected percentage before leaving the WPF UI thread, correcting the safely rolled-back first 1.19 live attempt.

## User controls

- `Limit CPU maximum processor state` is off by default.
- The enabled ceiling is selectable from 20% through 99%; the default is 80%.
- This is a global performance-state ceiling on the active Windows power plan. It is not a CPU utilization target and does not promise an exact clock speed.
- GPU limiting and screen dimming continue to work when CPU limiting is off.

## Safety lifecycle

1. The original active scheme identifier and exact AC/DC Maximum processor state values are read.
2. A bounded recovery journal is written before either power-plan value changes.
3. The requested percentage is treated only as a ceiling: an existing lower AC or DC value is never raised.
4. Both values are written, the scheme is activated, and exact read-back is required.
5. Restore, checkbox disable, normal shutdown, and startup recovery restore the exact original scheme and AC/DC values before deleting the journal.

If the optional CPU step fails after a verified GPU limit, AFK Power Saver attempts an immediate exact GPU rollback so a partially completed combined action is not accepted as success. If CPU recovery cannot be verified, new live actions are blocked while the recovery journal remains available for the next startup.

## Production cleanup

The former green fake-GPU button, simulated target selector, process runner, and simulation executables are absent from the production UI and package. The underlying simulation projects remain in the repository as non-production safety regression tests.
