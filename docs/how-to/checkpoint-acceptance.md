# Verify checkpoint and event recovery

[Documentation](../README.md)

Extend the session checkpoint probe to pending and uncertain actions. First read [save
and resume a session](save-and-resume.md) for the base probe.

Run commands from the repository root. Native probes require a disposable prepared
profile and their stated fixture; run `--help` for the selected script. Keep outputs
under a fresh `.rimgovernor/` directory, preserve failures, and never replace installed DLLs
while any RimWorld instance is running. Container inputs use private snapshots.

Add `--mixed` to `scripts/session_checkpoint_acceptance.py` for an issued shell slot,
its unissued material reservations, a pending growing zone and pending work setting. The
paired native restart must retain exact controller state and native building IDs while
staying in Manual with no replay or stale manual requests. Add `--uncertain-zone` with
`--mixed` to issue ordinary zone creation and deliberately lose its successful receipt
before Hands receives it. The checkpoint must preserve the blocked action and
unconfirmed issued slot alongside the exact native zone identity, geometry and crop.
Paired restart and delivery of the obsolete queued request must retain that uncertainty
in Manual with zero native replay. This separately covers an issued non-idempotent
operation; a wholly pending zone does not. Add `--rewind` with `--mixed` to reload the
unchanged older native baseline after the paired restart. The probe requires later
blueprint identities to disappear, discards an explicitly delivered obsolete queued
request, and rejects an old-load native write without changing the observed building set
or leaving Manual.

Add `--delivery --retention` to verify native lease expiry, a dropped event-read
response followed by one durable delivery, paired restart, active-resume protection
and deletion of a separate checkpoint pair. Run this in a staged local Docker
worker with Linux GABS. `test_event_delivery.py` separately reopens SQLite after
fetched-but-undelivered events and acknowledged chat, and injects transaction failure
before acknowledgment. Add `--durable-events` with the current native companion to
exceed 128 events, kill the owned game, stage a fully written but unpublished row,
and verify exact journal recovery through paired restart. This variant has a
600-second wall-time bound for its native start/pause transitions and restart.

Run `python scripts/attached_checkpoint_acceptance.py --root <staged-private-root>`
inside a Linux worker to launch an external game through the GABS CLI, then verify
controller-only restart against its real PID/birth, unchanged load/tick and chat.
It also advances the native game and verifies stale attached resume refusal.
Only the acceptance launcher explicitly stops that external fixture game.

`test_migration_boundaries.py` executes the Windows migration orchestrator with
faults at all ten suspension/backup/takeover/save/stop/start boundaries and native/API
doubles. `test_windows_process.py` separately exercises actual Windows process
suspension, parent crash, watchdog recovery and PID/birth mismatch refusal. These
checks cover orchestration and OS ownership; native paired-save and external-game
outcomes are established by the Docker probes above, not by the doubles.

## Related reading

[Choose tests](choose-tests.md) · [Test evidence explained](../explanation/testing.md) ·
[Backlog](../BACKLOG.md)
