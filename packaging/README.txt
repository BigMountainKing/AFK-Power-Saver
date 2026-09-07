AFK Power Saver 1.25.0 Release Candidate
========================================

AFK Power Saver reduces the enabled GPU and CPU limits and can dim selected displays,
then restores the exact original settings.

Install
-------

1. Run AFK-Power-Saver-1.25.0-Setup.exe.
2. Open AFK Power Saver from the Start menu.
3. Approve the one startup UAC prompt for the isolated GPU helper.

.NET 10 Desktop Runtime (x64) is required. The .NET 10 SDK also includes it.

Uninstall
---------

Open Windows Settings > Apps > Installed apps, find AFK Power Saver, and select Uninstall.
The uninstaller removes the app files, Start-menu shortcut, and launch-at-startup entry.
Saved preferences are retained for a future installation.

Safety
------

- The desktop app remains a normal-user process.
- Elevated GPU writes stay isolated in the recovery-first helper.
- X and Alt+F4 ask whether to minimise or safely exit unless a close preference is remembered.
- Safe exit restores active GPU/CPU settings and removes screen dimming before the process ends.
- The notification-area menu can activate or restore the profile, configure enabled features, refresh GPU data, reopen the window, or exit safely.
- Tips are optional and do not unlock features.
- NVIDIA control uses NVML. Experimental AMD Radeon control uses the ADLX component installed with the AMD display driver and displays limits as a percentage of the factory setting.
- AMD support is contract-tested but has not yet been verified on physical Radeon hardware.

What's new in 1.25.0
-------------------
- Independent CPU/display profiles survive GPU refresh.
- GPU recovery handles interrupted journal writes and already-restored hardware.
- Automatic restoration retries; each resource can recover independently.
- A normal-user CPU companion attempts recovery after desktop termination.
- CPU restoration preserves a different power plan selected by the user.
- AMD control requires a verified driver default; physical Radeon qualification remains pending.
