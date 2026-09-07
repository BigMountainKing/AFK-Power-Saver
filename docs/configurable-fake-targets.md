# Configurable fake targets

Milestone 1.5 adds target selection without introducing arbitrary elevated power values.

## Available profiles

The UI offers four immutable simulation profiles:

- `eco-250` — 250 W;
- `eco-300` — 300 W;
- `eco-350` — 350 W;
- `eco-400` — 400 W.

Each profile has a unique compile-time run ID and a fixed target. The 400 W profile retains the milestone 1.4 run ID so an older pending fake pause remains recoverable after upgrading.

## Live UI validation

After the sanitized read-only GPU probe completes, the UI accepts a profile only when its target:

- is inside the live driver minimum and maximum;
- is strictly below the current driver limit;
- exactly matches an allow-listed profile.

For the verified RTX 4090 constraints of 450 W current and 150–600 W permitted, all four profiles are valid. A selection equal to the current limit or below a different GPU's live minimum is rejected before UAC.

## Elevated boundary

The desktop passes the launcher a profile ID, never numeric input. The normal-user launcher independently resolves that ID to a fixed run ID. The elevated helper independently resolves the run ID to its own fixed profile and constructs the recovery snapshot itself.

Before activation, the helper checks the four exact protected profile directories:

- zero pending profiles allows activation of the requested profile;
- one pending profile forces restoration of that profile, even if the UI requests another;
- more than one pending profile fails closed.

This restoration-first rule prevents profile changes from hiding or replacing recovery-pending state.

## Evidence and receipts

The strict transcript binds the resolved profile ID to its expected watt value and the observed fake limit. The current-user receipt stores state plus an allow-listed profile ID for display after restart. Neither can choose the elevated operation; unknown, duplicated, or conflicting mappings fail closed.

All four profiles still operate only on the integrity-checked file-backed fake controller. Real NVIDIA access remains read-only.
