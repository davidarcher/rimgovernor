# Launch a prepared colony

[Documentation](../README.md)

The Go controller is the sole production runtime (G01.13). Autopilot (routine work)
needs no model and no player text input; run:

```powershell
.\launch.cmd
```

This builds `go/cmd/rimgovernor` if missing, reuses the built dashboard assets, and
opens http://127.0.0.1:8787 in autonomous play.
Enable Run in background in RimWorld. `launch-bridge.ps1` forwards to the same
launcher. Launch uses the prepared GABS profile and preserves your normal saves and
mod selection — see [setup](setup.md) to prepare it first.

```powershell
.\launch.cmd -Observe       # Observation-only dashboard; no player writes
.\launch.cmd -NoBrowser
.\launch.cmd -Port 8788     # Use a different local port
.\launch.cmd -Rebuild       # Force-rebuild the Go binary before starting
```

Each run opens a fresh Go state database (timestamped under `.rimgovernor/go/`); it
does not reuse a controller already listening on the target port, so stop an existing
session (or pick a different `-Port`) before starting a new one.

## Natural-language chat

Chat appears in the dashboard when the controller runs with `--chat-model`
(and `--chat-base-url` pointing at LM Studio's local server). It answers
questions about the autopilot and applies at most one policy nudge per
message; see [controls](controls.md#give-a-request).

## Related reading

[Sessions and recovery](../developers/architecture/sessions-and-recovery.md) · [Save and
resume](save-and-resume.md)
