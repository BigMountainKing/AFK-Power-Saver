# Desktop percentage live toggle

Milestone 1.13 replaces the old absolute-watt dropdown with a GPU-relative percentage slider and makes the global hotkey assignable. The green fake button and idle preview remain unable to invoke real hardware.

The read-only probe must prove one unambiguous supported NVIDIA or AMD GPU with a lower setting below its default. For NVIDIA, the slider minimum is `floor(minimum/default × 100)`, its maximum is 99%, and the resolved whole-watt value is displayed. For AMD, ADLX reports a percentage adjustment rather than an absolute watt cap, so the UI displays the resulting percentage of the factory setting and snaps to the driver-reported step. NVIDIA is preferred if both backends are available.

AMD Radeon support is experimental until it has been qualified on physical hardware. It requires the ADLX component installed with a current AMD display driver. AFK Power Saver does not reset the rest of the AMD tuning profile: if ADLX reports that a factory reset is required because automatic tuning is active, the action fails safely and asks the user to disable that tuning mode.

One startup UAC prompt creates the mutually process-ID-verified elevated broker. Later button/hotkey requests carry only `percent-N`; the elevated one-shot helper independently reads constraints, resolves the same target, journals the exact GPU default and target, and chooses recovery before activation. Restoration uses the journaled default rather than a hard-coded 450 W value.

Success requires a normal helper exit, authenticated operation, exact independent read-back, correct journal stage, and retained artifacts while paused or cleaned artifacts after restoration. Window closure asks the broker to restore before exiting.

## RTX 4090 test values

For the qualified 150–600 W device with a 450 W default:

- slider range: 33–99%;
- 33% resolves to the 150 W driver minimum;
- 89% resolves to 400 W;
- restoration remains exactly 450 W.

Close competing GPU tuning tools before testing. Use the displayed default for an independent administrator rollback, for example `nvidia-smi --power-limit=450` on the qualified RTX 4090.
