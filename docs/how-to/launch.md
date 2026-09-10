# Launch a prepared colony

[Documentation](../README.md)

Autopilot runs without a model. For interactive chat, start LM Studio's local server
with the configured model loaded (Qwen3.5-9B by default), then run:

```powershell
.\launch.cmd
```

The dashboard opens at http://127.0.0.1:8787 in Manual mode. If RimWorld is not running,
the launcher starts the prepared isolated eight-tribal fixture. If a native bridge
game/controller is already running, it is reused. Enable Run in background in RimWorld.
Automate pauses for its initial review, then resumes.

```powershell
.\launch.cmd -FreshGame              # Require a new fixture; close existing game/controller first
.\launch.cmd -NoGame                 # Connect to an already running bridge game
.\launch.cmd -NoBrowser
.\launch.cmd -Model qwen3.5-4b        # Use this model if loaded in LM Studio
```

`launch-bridge.ps1` forwards to the same launcher. There is no backend selector. The old
NormalGame/QuickTest flags are retired; this launcher uses the prepared GABS profile. It
does not silently alter your normal saves or mod selection.

## Configure local model roles

Interactive chat uses the `strategist` model role; autopilot needs no inference. To
configure a generic 4B analyst, copy/edit `config/models.example.json`, then restart the
controller with:

```powershell
.\launch.cmd -ModelsConfig config\models.example.json
```

Role names are `strategist`, `analyst`, `architect`, and `critic`. All optional roles
can be omitted or point at the same loaded model. Consultations happen only when the
strategist asks a specific question; there is no domain-manager fan-out.

## Related reading

[Sessions and recovery](../explanation/sessions-and-recovery.md) · [Save and
resume](save-and-resume.md)
