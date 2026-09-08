# Native control checkpoint

Implemented: durable write-ahead draft ownership scoped to the current native load token. Draft/goto/attack/tend resolve the pawn before issuing the action; already drafted player pawns are not claimed. Confirmed undrafted receipts clear ownership. Manual mode, review failure, and orderly shutdown attempt pause and release tracked drafts, verifying the final draft state. Uncertain cleanup retains the obligation and emits a blocker.

Validation: 26 controller tests pass, including lost write receipts, partial cleanup failure, and pause failure. No new live gameplay validation or deployment in this checkpoint.

Remaining: native event-aware clock lease and interrupt handling; human pause/speed override detection; combat completion cleanup while Automate remains on; observed movement/combat live test; live trading. Process-crash pausing needs the native lease, not this Python shutdown path. Ownership cannot distinguish a human undraft/redraft between observations.

Then: visual second opinion and bounded read-only data scouts as described in the instruments audit.


## Event-aware clock slice

Implemented the in-game supervisor and independent controller heartbeat. The
planner uses `control_clock` to start normal game time with native danger monitoring,
not a wall-time budget for model turns. External pause/speed override and expired
lease stop automatic resumption. Clock events enter the existing dashboard feed
as Game observations and invalidate pending planner actions. Explicit player
Automate clears the hold. Danger alone leaves the planner able to inspect and
respond, without the old global-hostiles construction block.

Live checks passed: 1-second test lease expires and verifies pause without a
heartbeat; an external pause prevents restart until explicit release; an actual
pawn reaches a nearby destination; the runtime ledger undrafts that pawn and
pauses on halt. Production lease is 15 seconds, renewed every 3 seconds.

Still remaining: combat completion cleanup while Automate stays on (the native
hostiles-cleared event is advisory); real combat and live trading validation;
visual second opinion and bounded read-only data scouts. No claim of winning a
fight is made by the movement test. Cleanup ownership still cannot distinguish a
human undraft/redraft between observations.

## Selected stand-down during Automate

The strategist can now commit `stand_down` with exact pawn IDs. Its context lists
current-load AI-owned drafts. Deterministic Hands releases only the selected owned
drafts, confirms pawn identity and final draft state through native reads, and
keeps Automate enabled. Player-owned and other-load drafts are untouched. This is
an explicit strategic decision; a hostiles-cleared event does not automatically
cancel every drafted job, and distant cave insects do not gate cleanup.

Partial failures remain durable cleanup obligations and block the step with native
failure details. Retrying first reads current state: a lost successful undraft
receipt does not require another undraft. New direction/load/plan revisions stop
remaining writes. Cancelling a plan still does not silently cancel game orders.

Validation: 52 controller tests; `scripts/native_stand_down_smoke.py` passed on the
headless eight-tribal fixture. It drafted two pawns through separate controller and
test-player paths, executed the committed cleanup step, verified only the owned
pawn was undrafted, and verified Automate remained on. Fixture drafts were cleaned
up afterward. No model call or actual fight was part of this check.

Next combat acceptance: a controlled threat scenario with observed attack outcome,
injury interruption and strategy-selected stand-down. Victory detection, rescue
completion and human undraft/redraft ownership ambiguity remain open.
