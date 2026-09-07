# Desktop read-only GPU integration

Milestone 1.1 brings live GPU information into the EcoPause desktop window without bringing hardware access into the UI process.

## Process boundary

`EcoPause.Desktop.exe` runs as the normal user and starts `EcoPause.Probe.exe --json` without elevation. The probe owns the read-only NVML interaction. The desktop receives only a sanitized JSON report and validates it before displaying anything.

The desktop waits at most ten seconds, accepts at most 64 KiB of standard output and 4 KiB of standard error, and terminates only that child process if the boundary fails. Any standard-error content, malformed JSON, inconsistent exit status, unknown field, oversized value, duplicate GPU index, or implausible power value fails closed.

The accepted display fields are:

- provider and non-identifying status text;
- GPU index and model name;
- current, default, minimum, and maximum power limits;
- optional current power usage.

Unique device identifiers, serial numbers, fingerprints, paths, native function names, and unrecognized extensions are outside the contract.

## UI behavior

The first read occurs after the window loads. **Refresh read-only GPU data** repeats the bounded probe. A failed read changes only the GPU card to an unavailable state; it does not crash the window or enable any alternative hardware path.

**Run safe fake lifecycle** remains the milestone 1.0 simulation. It launches the mutually verified UAC helper, exercises authenticated activation and restoration against fake hardware, and requires the complete transcript evidence including `Real NVIDIA access: NONE`.

## Deliberately absent

- NVIDIA setters or mutable native bindings;
- a power-limit control in the UI;
- automatic idle activation;
- background polling or telemetry;
- persistence of live GPU data.
