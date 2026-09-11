# Player guide

[All docs](../README.md)

RimGovernor helps run a colony under normal RimWorld rules. You can let Autopilot
handle routine needs, give explicit requests through chat, or take control yourself.
It is still a development build; setup requires a prepared game profile and save.

1. [Set up the game and controller](setup.md).
2. [Launch your prepared colony](launch.md).
3. [Use the dashboard and controls](controls.md).
4. [Save and resume together](save-and-resume.md).

## When something stops

- **Manual:** routine automation is off. Choose Automate when ready to resume.
- **Work is blocked:** inspect the goal in Work and its reason before changing
  priorities. Issued orders still depend on available pawns, materials and access.
- **Chat is unavailable:** start LM Studio's local server and load the configured
  model. Autopilot can run independently.
- **No game image:** a headless session supplies colony data only. Paused video
  and paused simulation are separate controls.
- **Launch fails:** check the [setup prerequisites](setup.md).
  Setup does not download the game, GABS or the required baseline save.

The [backlog](../BACKLOG.md) lists known gaps. A short successful run does not
establish reliable survival across every seed, season or threat.
