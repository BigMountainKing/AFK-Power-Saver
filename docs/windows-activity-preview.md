# Windows activity preview

Milestone 1.2 answers one question without taking action: **would EcoPause consider this PC unattended now?**

## Signals

The normal-user desktop process asks Windows for the aggregate duration since the last keyboard or mouse input. It also listens for the current user's session lock and unlock notifications. The value is refreshed once per second and kept only in memory.

EcoPause does not install an input hook and does not read or retain:

- keys, mouse buttons, or pointer positions;
- foreground windows, applications, or process names;
- usernames or session identifiers;
- a timeline or activity log.

## Preview policy

The fixed preview threshold is five minutes:

- while the unlocked session is below five minutes idle, the UI counts down and reports `Active use`;
- at five minutes idle, the UI reports `Would pause now — preview only`;
- a session lock reports `Would pause now` immediately;
- new input or session unlock returns the preview to active monitoring when appropriate.

The pure decision function validates non-negative idle time and a positive threshold. Boundary cases are covered by dependency-free tests.

## No action boundary

The panel always displays `AUTO ACTION OFF`. Its observer updates text and a status dot only. It has no reference to `SimulationProcessRunner`, does not click or call the manual lifecycle button, and cannot send commands to the elevated helper.

The existing green button remains the only way to begin the fake lifecycle. Real GPU writes remain absent.
