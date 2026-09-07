# AFK Power Saver documentation

## Install and use

Download the installer from [GitHub Releases](https://github.com/BigMountainKing/AFK-Power-Saver/releases/latest). Requires Windows 11 x64 and .NET 10 Desktop Runtime. GPU limiting requires a supported NVIDIA driver or AMD Radeon driver with ADLX. AMD support remains experimental and has not been validated on physical Radeon hardware. The installer is currently unsigned.

Launch the app normally and approve the GPU helper's UAC prompt. Select your power limit and use the button or assigned hotkey to toggle it. CPU limiting, screen dimming and idle activation are optional. A profile activated by the idle timer restores when activity resumes; a manually activated profile stays under manual control.

CPU limiting changes Windows Maximum processor state; it is not a literal CPU utilisation cap. Screen dimming uses an overlay, so monitor power savings depend on the display technology.

## Build and test

Install the .NET 10 SDK specified in [global.json](../global.json) and Visual Studio with the .NET desktop and Desktop development with C++ workloads.

Run from the repository root in PowerShell:

```powershell
# Build and run all checks without changing hardware settings.
./packaging/Test-Safety.ps1

# Launch the desktop app.
dotnet run --project src/EcoPause.Desktop --configuration Release

# Build the installer and verify packaging, installation and removal.
./packaging/Build-Release.ps1
```

The installer is written to `artifacts/`. Launching the desktop app enables its normal hardware controls; the safety test script uses simulated hardware.

## Further reading

- [Current architecture and recovery behaviour](current-architecture.md)
- [Development reference, qualification commands and historical milestones](development-history.md)
- [Code signing](code-signing.md)
- [Security](../SECURITY.md)

The current architecture document describes version 1.25.0. Historical milestone procedures record earlier behaviour and are intended for development reference.
