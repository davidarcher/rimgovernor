# Save and resume

[Player guide](README.md)

Use Autopilot's **Save checkpoint and pause** to save the game together with its
controller goals, policies and conversation. Keep `game.rws`, `bridge.sqlite`
and `checkpoint.json` together and unedited.

## Restart an owned session

From the repository root:

```powershell
scripts/restart_session.ps1 -Port 8787
```

This saves and verifies the pair before restarting. Unsupported servers remain
running. Resume starts in Manual; choose Automate when ready. Keep the installed
game DLLs unchanged until all sessions have closed.

To resume a retained checkpoint manually, close the previous owned process,
activate the checkout's Python environment and run:

```powershell
python -m rimgovernor --resume <checkpoint.json> --port 8787
```

Replace `<checkpoint.json>` with the retained manifest path. Resume validates
the saved colony and map and restores the controller into a new database.

## Attached sessions

An attached controller with a known private profile uses the same commands, but
reconnects to the unchanged, paused external game. Keep that game running. If it
has loaded or advanced since the checkpoint, create a new checkpoint.

Developer details: [session contracts](../developers/contracts/session-contracts.md),
[checkpoint tests and retention API](../developers/testing/checkpoint-acceptance.md).
