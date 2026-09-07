# Persistent fake toggle

Milestone 1.4 turns `Ctrl+Shift+F3` into a two-state simulation that survives process restarts. Milestone 1.5 extends it with four fixed fake target profiles.

## First press: pause

The normal-user launcher creates a randomly named current-user-only pipe and starts the fixed elevated helper through UAC. After mutual Windows process-ID verification, the helper creates its own administrator-only run directory and a fixed recovery snapshot for a 450 W original and the selected allow-listed fake target.

The helper—not the UI—selects `ActivatePrepared`. The authenticated command persists `Prepared`, writes the file-backed fake limit, reads back exactly 400 W, and persists `Applied`. The helper exits normally while the protected state remains recovery-pending.

The UI accepts `Paused` only with exact evidence for authenticated activation, 400 W, `Applied`, retained protected artifacts, a passing summary, and no NVIDIA access.

## Second press: restore

The new elevated helper discovers the fixed protected run directory and validates its ACL-safe path, journal integrity, full fixed profile, and fake state. It selects `RestorePending`, irrespective of what the UI displays.

Restoration writes or verifies exactly 450 W, records `Restored`, and deletes only `recovery.json`, `fake-hardware.json`, and the now-empty known run directory. Recursive deletion is not used.

The UI accepts `Restored` only with exact evidence for authenticated restoration, 450 W, `Restored`, successful cleanup, a passing summary, and no NVIDIA access.

## Restart recovery

After a successful toggle, the normal-user launcher atomically stores a tiny receipt under the current user's local application data. It contains only `Paused` or `Restored`. On restart, the UI uses it to choose its wording, but labels it display-only.

The receipt is never trusted for action. If it is deleted, corrupted, or changed, the next hotkey still behaves safely because the elevated helper decides from the administrator-only protected directory. A stale `Prepared` journal is also recoverable, covering interruption before the helper could record `Applied`.

## Still fake

The persistent controller changes only an integrity-checked JSON simulation file. Neither the launcher nor helper references `EcoPause.Hardware.Nvidia`, and the NVIDIA provider still contains no mutation functions.
