# Launch a prepared colony

[Documentation](../README.md)

The Go controller is the production default (G01.12). Autopilot (routine work) needs
no model and no player text input; run:

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

## Natural-language chat is not yet available here

The Go dashboard shows colony state and structured building/routine/player controls
(no free-text message box); it does not call a local model. Interactive chat
(typed requests interpreted by a local model) still runs only through the Python
controller, which remains available directly for this and as a rollback path during
the G01.13 removal window:

```powershell
.\launch.ps1                          # Python controller; interactive chat
.\launch.ps1 -FreshGame               # Require a new fixture; close existing game/controller first
.\launch.ps1 -NoGame                  # Connect to an already running bridge game
.\launch.ps1 -Model qwen3.5-4b        # Use this model if loaded in LM Studio
```

The dashboard opens at http://127.0.0.1:8787 in Manual mode; it reuses an
already-running native bridge game/controller instead of requiring a fresh one.
This chat gap is tracked for the Go controller; it is not a rewrite gate for G01.12.

### Configure local model roles (Python controller)

Interactive chat uses the `strategist` model role; autopilot needs no inference. To
configure a generic 4B analyst, copy/edit `config/models.example.json`, then restart
with:

```powershell
.\launch.ps1 -ModelsConfig config\models.example.json
```

Role names are `strategist`, `analyst`, `architect`, and `critic`. All optional roles
can be omitted or point at the same loaded model. Consultations happen only when the
strategist asks a specific question.

## Related reading

[Sessions and recovery](../developers/architecture/sessions-and-recovery.md) · [Save and
resume](save-and-resume.md)
