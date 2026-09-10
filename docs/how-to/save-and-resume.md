# Save and resume a session

[Documentation](../README.md)

Use paired native/controller checkpoints to resume an owned colony with preserved
intent.

Run commands from the repository root. Native probes require a disposable prepared
profile and their stated fixture; run `--help` for the selected script. Keep outputs
under a fresh `.rimgovernor/` directory, preserve failures, and never replace installed DLLs
while any RimWorld instance is running. Container inputs use private snapshots.

On a checkpoint-capable owned session, use Autopilot's **Save checkpoint and pause** or
run `scripts/restart_session.ps1 -Port 8787`. The restart command first saves and
verifies the native game and controller snapshot; unsupported older servers remain
running. A worktree can supply `-Python <venv-python.exe>`. Keep the existing game DLLs
installed until all sessions have closed.

For a retained checkpoint, run `python -m rimgovernor --resume <checkpoint.json> --port
8787`. Close the previous owned process before manually resuming. This restores into a
new SQLite database and starts in Manual. Resume preserves the saved colony/map and
allows at most one native loading tick with pause-on-load enabled. Larger changes fail
closed. Do not edit or separate `game.rws`, `bridge.sqlite` and `checkpoint.json`.

Attached controllers with a known private profile use the same commands. Their
checkpoints reconnect to the unchanged, paused external game; they never stop or
reload it. Keep that game running. If its load or tick changes, create a new checkpoint
instead of attempting to rewind it through an attached checkpoint.

List retained pairs with `GET /api/session/checkpoints`. To remove a pair, send
`POST /api/session/checkpoints/delete` with `X-RimGovernor: 1` and JSON containing the
current `session_id` and its exact `manifest_path`. The active resume pair, restarting
sessions, damaged pairs and unexpected directory contents are protected. Other pairs
and the native profile's saves remain intact; retention is explicit, without expiry.

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

## Related reading

[Choose tests](choose-tests.md) · [Test evidence explained](../explanation/testing.md) ·
[Backlog](../BACKLOG.md)
