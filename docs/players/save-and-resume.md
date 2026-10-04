# Save and resume

[Player guide](README.md)

The dashboard's save/load controls write and read a normal RimWorld save
(`POST /api/lifecycle`), the same file the game itself would produce. Controller
state (standards, projects, incidents and progress) lives separately, in the Go controller's own
SQLite database under `.rimgovernor/go/`.

## Restart a session

Press **Stop** (or **Restart**) in the launcher, then **Play**. With the State
setting on **Continue last state** (the default), the controller reopens the
newest `.rimgovernor\go\state-<timestamp>.sqlite`; **Fresh state** starts a new
one.

By default the controller starts paused; choose **Resume** in the dashboard
when ready. Turn on **Start bot automatically on load** to run the bot for the
loaded colony at startup and again after every load.

Loading a save (from the dashboard or in-game) starts a fresh review of the
loaded colony; concerns are re-derived from what the controller observes, and
only orders that were already issued but never confirmed are followed up.
Keep the installed game DLLs unchanged until all sessions have closed.

## Attached sessions

An attached controller with a known private profile uses the same launcher, but
reconnects to the unchanged, paused external game. Keep that game running.
