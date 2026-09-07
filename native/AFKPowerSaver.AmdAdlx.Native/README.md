# AMD ADLX bridge

This small x64 bridge calls the `amdadlx64.dll` installed with AMD's Windows display driver. It exposes only the read, range, identity, and set operations needed by AFK Power Saver.

The bridge deliberately does not call ADLX `ResetToFactory`. If AMD automatic tuning prevents a power-limit change, the operation fails safely rather than resetting unrelated fan, clock, or voltage tuning.

The application does not redistribute AMD's DLL, SDK headers, or sample source. AMD hardware support therefore requires a compatible Radeon driver with ADLX and manual power tuning support.
