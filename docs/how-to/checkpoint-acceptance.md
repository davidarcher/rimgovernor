# Verify mixed checkpoint recovery

[Documentation](../README.md)

Extend the session checkpoint probe to pending and uncertain actions. First read [save
and resume a session](save-and-resume.md) for the base probe.

Run commands from the repository root. Native probes require a disposable prepared
profile and their stated fixture; run `--help` for the selected script. Keep outputs
under a fresh `.rimbot/` directory, preserve failures, and never replace installed DLLs
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

## Related reading

[Choose tests](choose-tests.md) · [Test evidence explained](../explanation/testing.md) ·
[Backlog](../BACKLOG.md)
