# Save and resume

[Player guide](README.md)

The dashboard's save/load controls write and read a normal RimWorld save
(`POST /api/lifecycle`), the same file the game itself would produce. Controller
state (goals, policies and progress) lives separately, in the Go controller's own
SQLite database under `.rimgovernor/go/`.

## Restart a session

Stop the running `rimgovernor` process (or the launcher window), then relaunch:

```powershell
.\launch.cmd
```

Each launch opens a fresh, timestamped state database by default and does not
reuse a controller already listening on the target port — stop an existing
session first. To reuse a specific prior state database instead of starting
fresh, pass it explicitly:

```powershell
.\launch-go.ps1 -State .rimgovernor\go\state-<timestamp>.sqlite
```

Resume starts in Manual; choose Automate when ready. Keep the installed game DLLs
unchanged until all sessions have closed.

## Attached sessions

An attached controller with a known private profile uses the same launch
commands, but reconnects to the unchanged, paused external game. Keep that game
running.
