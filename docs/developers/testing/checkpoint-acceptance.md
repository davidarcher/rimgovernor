# Verify checkpoint and event recovery

[Documentation](../../README.md)

Use the base probe below, then select the variants needed for the change.

[Native test prerequisites](README.md#native-test-prerequisites).

## Base checkpoint and archive probe

Run `python scripts/session_checkpoint_acceptance.py --source-root <prepared-root>
--output <new-output>` for native save, process shutdown, restart and state comparison.
Add `--rendered` for the visible profile. The probe verifies a PLAYER food goal, policy
and conversation, unchanged native colony identity, a new load token and no model calls.
Checkpoint tampering, failed saves, stale direction and unresolved drafts are also
covered by focused tests. Add `--archive` to execute and verify a native
hauling-priority change through Hands, retire its completed action, and check the exact
archived receipt and native assignment after paired restart. Reintroducing its old
identity must be rejected with no native action. This check uses no inference and does
not certify mixed pending construction or interrupted non-idempotent actions.

## Pending actions and event recovery

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

[Testing](README.md) · [Issues](https://github.com/davidarcher/rimgovernor/issues)

## Checkpoint retention API

List retained pairs with `GET /api/session/checkpoints`. To remove a pair, send
`POST /api/session/checkpoints/delete` with `X-RimGovernor: 1` and JSON containing the
current `session_id` and its exact `manifest_path`. The active resume pair, restarting
sessions, damaged pairs and unexpected directory contents are protected. Other pairs
and the native profile's saves remain intact; retention is explicit, without expiry.
