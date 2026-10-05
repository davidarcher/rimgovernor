# Save and resume

[Player guide](README.md)

The launcher's **Saved game** picker loads a normal RimWorld save
(`POST /api/lifecycle/load`); saves are the same files the game itself produces. Controller
state (standards, projects, incidents and progress) lives separately, in the Go controller's own
SQLite database under `.rimgovernor/go/`.

## Restart a session

Press **Stop** (or **Restart**) in the launcher, then **Play**. With the State
setting on **Continue last state** (the default), the controller reopens the
newest `.rimgovernor\go\state-<timestamp>.sqlite`; **Fresh state** starts a new
one.

The bot runs for the loaded colony at startup and again after every load.

Loading a save (from the launcher or in-game) starts a fresh review of the
loaded colony; concerns are re-derived from what the controller observes, and
only orders that were already issued but never confirmed are followed up.
Keep the installed game DLLs unchanged until all sessions have closed.

## Attached sessions

An attached controller with a known private profile uses the same launcher, but
reconnects to the unchanged, paused external game. Keep that game running.
