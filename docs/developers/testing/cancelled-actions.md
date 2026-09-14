# Verify retained cancelled actions

[Documentation](../../README.md)

Check that cancelled controller work stays cancelled across native observations and
restart.

[Native test prerequisites](README.md#native-test-prerequisites).

Run `scripts/cancelled_action_acceptance.py --checkpoint <checkpoint.json> --output
<new-directory> --port 8788` with the controller on `PYTHONPATH`. The probe verifies the
paired checkpoint hashes, copies its unchanged native save into a disposable visible
profile, and starts with an empty controller plan. Watch its dashboard on the selected
port. It issues a room through the semantic player command path, cancels its goal while
blueprints remain, and verifies an unrelated research order completes without changing
cancelled receipts or native orders. It retains `result.json` and stops its owned
game/server. Add `--restart` to save the cancelled room and controller database as a
verified pair, stop the owned game, and resume into a new database before the research
command. This checks the complete plan and conversation, a new load token, the saved
tick (allowing one loading tick), unchanged native blueprint identities, empty pending
manual requests and no reclaimed draft ownership. Repeating the room intent must return
its cancelled state without issuing another order. The dashboard briefly disconnects
during this disposable restart. This is zero-inference command/executor acceptance; it
does not test language interpretation, completed construction, native blueprint
cancellation, or save rewind. Unit tests separately cover serialized plan restoration.

## Related reading

[Testing](README.md) · [Issues](https://github.com/davidarcher/rimgovernor/issues)
