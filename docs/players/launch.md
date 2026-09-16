# Launch a prepared colony

[Documentation](../README.md)

The Go controller is the sole production runtime (G01.13). Autopilot (routine work)
needs no model and no player text input; run:

```powershell
.\launch.cmd
```

This builds `go/cmd/rimgovernor` if missing, reuses the built dashboard assets, and
opens http://127.0.0.1:8787 with player building/draft/routine control enabled.
Enable Run in background in RimWorld. `launch-bridge.ps1` forwards to the same
launcher. Launch uses the prepared GABS profile and preserves your normal saves and
mod selection — see [setup](setup.md) to prepare it first.

```powershell
.\launch.cmd -ReadOnly      # Observation-only dashboard; no player writes
.\launch.cmd -NoBrowser
.\launch.cmd -Port 8788     # Use a different local port
.\launch.cmd -Rebuild       # Force-rebuild the Go binary before starting
```

Each run opens a fresh Go state database (timestamped under `.rimgovernor/go/`); it
does not reuse a controller already listening on the target port, so stop an existing
session (or pick a different `-Port`) before starting a new one.

## Natural-language chat is not currently available

The Go dashboard shows colony state and structured building/routine/player controls
(no free-text message box); it does not call a local model. See
[issue #46](https://github.com/davidarcher/rimgovernor/issues/46) for the
status of rebuilding it in Go.

## Related reading

[Sessions and recovery](../developers/architecture/sessions-and-recovery.md) · [Save and
resume](save-and-resume.md)
