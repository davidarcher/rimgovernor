# Migrate an owned legacy session

[Documentation](../README.md)

Use this Windows procedure only for an owned legacy server without the checkpoint
endpoint.

Run commands from the repository root. Native probes require a disposable prepared
profile and their stated fixture; run `--help` for the selected script. Keep outputs
under a fresh `.rimbot/` directory, preserve failures, and never replace installed DLLs
while any RimWorld instance is running. Container inputs use private snapshots.

For an owned Windows legacy server without the checkpoint endpoint, first install the
current Python source in the checkout reported by `/api/health`. Keep native DLLs
unchanged. With that checkout's `controller` on `PYTHONPATH`, run:

```powershell
python scripts/migrate_legacy_session.py --port 8787 --root <existing-bridge-root> --database <existing-bridge.sqlite> --source <serving-checkout>
```

The command pauses routine operation and briefly suspends the old backend while copying
its database and handing GABS ownership to the migrator. A detached watchdog resumes the
backend if the migration process dies. This does not restore the old GABS connection
after takeover. The game is saved through the native tool, then stopped and resumed
using the paired checkpoint. Success requires unchanged shared plan, settings and
conversation, the same colony/map, and at most one loading tick. Leave the colony in
Manual until the player explicitly resumes it.

Artifacts and logs remain under `<root>/migrations/<id>`. Before native shutdown, an
interrupted handoff can be retried with the same arguments plus
`--recover-disconnected`; this bypasses the disconnected old control endpoint but still
requires its Manual/paused state, matching database, native load and tick. After native
shutdown, use the retained checkpoint's normal `--resume` command instead. Verify the
old server process has exited before starting a replacement on its port. Do not enable
automation or send chat through a disconnected legacy dashboard. There is no
checkpoint-only takeover that transparently returns ownership.

Inspect `phase` and `recovery` in the retained report before choosing a recovery
command. `game_stop_pending` means the stop outcome is uncertain: inspect native
liveness first. `game_stopped`, `controller_stopped` and `replacement_started` use
the retained checkpoint. Earlier takeover/save phases require explicit takeover
recovery. A missing or mismatched PID birth fingerprint requires a regenerated
private profile; never substitute executable-name cleanup.

## Related reading

[Choose tests](choose-tests.md) · [Test evidence explained](../explanation/testing.md) ·
[Backlog](../BACKLOG.md)
