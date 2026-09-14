# RimGovernor

A local RimWorld colony controller. Autopilot handles routine colony needs;
player chat uses a local model to turn requests into game work. The dashboard
shows priorities, plans and colonists while RimWorld runs the simulation.

## Play

Start with [setup](docs/players/setup.md), then launch from the repository root:

```powershell
.\launch.cmd
```

Open [the dashboard](http://127.0.0.1:8787). It starts in Manual; choose Automate
to enable routine control. Autopilot needs no model. Chat needs the configured
model loaded in LM Studio.

This is a development setup requiring licensed RimWorld files, native mods,
GABS and a prepared save. See the [player guide](docs/players/README.md) for
controls, saving and troubleshooting. Broader survival coverage remains tracked in
[GitHub issues](https://github.com/davidarcher/rimgovernor/issues).

## Develop

Use the [developer guide](docs/developers/README.md) to find the architecture,
source and checks for your change. Go runs the production controller
(`launch.cmd`/`launch-go.ps1`, G01.12); React/TypeScript runs the dashboard, and C#
supplies native game tools through GABS/RimBridgeServer. Python (`launch.ps1`)
remains available directly for interactive natural-language chat and as a rollback
path until [G01.13](https://github.com/davidarcher/rimgovernor/issues/33) removes it.
See [the Go module guide](go/README.md) for the current capability boundary.

You can run checks without installing the game:

```powershell
python scripts/container_checks.py --workers 1 --image rimgovernor-checks:my-task --output .rimgovernor/docker-checks-01
```

Requires Python 3.12+ and Linux Docker. Use a fresh output directory. See
[Docker checks](docs/developers/testing/docker-checks.md) for focused runs and results.

[All docs](docs/README.md) · [Working agreement](AGENTS.md)
