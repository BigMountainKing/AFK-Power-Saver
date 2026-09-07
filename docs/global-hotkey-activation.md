# Assignable global hotkey

Milestone 1.13 lets the normal-user desktop assign the live toggle from curated modifier and key lists. The default remains `Ctrl+Shift+F3`; `Alt+F3` is not selected by default because it commonly conflicts with NVIDIA overlays.

The choice is stored in `%LocalAppData%\EcoPause\settings.json`. A malformed file falls back to the default. EcoPause uses Windows `RegisterHotKey` with key-repeat suppression; it does not install a keyboard hook or inspect unrelated input. Applying a new shortcut reports a conflict and attempts to preserve the old registration if another program already owns it.

After the one startup UAC broker is ready, the assigned shortcut enters the same guarded path as the amber button. It sends only the current canonical `percent-N` profile, lets the protected journal choose recovery first, requires complete transcript evidence, and finishes with a separate read-only GPU refresh.

The idle preview never synthesizes the hotkey. No service, scheduled task, startup registration, or saved credential is created. Authorization lasts only while the app and its mutually verified pipe remain alive.

## Test procedure

Start EcoPause and approve its one UAC prompt. Choose a modifier/key pair and click **Apply hotkey**. With another application focused, press the shortcut once and require a verified percentage pause. Press it again and require exact restoration to the GPU's displayed default, with no additional dialog or UAC prompt.
