# Native control checkpoint

Implemented: durable write-ahead draft ownership scoped to the current native load token. Draft/goto/attack/tend resolve the pawn before issuing the action; already drafted player pawns are not claimed. Confirmed undrafted receipts clear ownership. Manual mode, review failure, and orderly shutdown attempt pause and release tracked drafts, verifying the final draft state. Uncertain cleanup retains the obligation and emits a blocker.

Validation: 26 controller tests pass, including lost write receipts, partial cleanup failure, and pause failure. No new live gameplay validation or deployment in this checkpoint.

Remaining: native event-aware clock lease and interrupt handling; human pause/speed override detection; combat completion cleanup while Automate remains on; observed movement/combat live test; live trading. Process-crash pausing needs the native lease, not this Python shutdown path. Ownership cannot distinguish a human undraft/redraft between observations.

Then: visual second opinion and bounded read-only data scouts as described in the instruments audit.
