# RimGovernor

A local RimWorld colony controller. Autopilot handles routine colony needs and
building/draft/routine player control. The dashboard shows priorities, plans
and colonists while RimWorld runs the simulation. Natural-language player chat
(a local model that explains the autopilot and nudges its policies) turns on
with `--chat-model`.

## Play

Start with [setup](docs/players/setup.md), then launch from the repository root:

```powershell
.\launch.cmd
```

Open [the dashboard](http://127.0.0.1:8787). It starts in Manual; choose Automate
to enable routine control. Autopilot needs no model.

This is a development setup requiring licensed RimWorld files, native mods,
GABS and a prepared save. See the [player guide](docs/players/README.md) for
controls, saving and troubleshooting. Broader survival coverage remains tracked in
[GitHub issues](https://github.com/davidarcher/rimgovernor/issues).

## Develop

Use the [developer guide](docs/developers/README.md) to find the architecture,
source and checks for your change. Go runs the production controller
(`launch.cmd`/`launch-go.ps1`); React/TypeScript runs the dashboard, and C#
supplies native game tools through GABS/RimBridgeServer. See
[the Go module guide](go/README.md) for building, running and testing it.

You can run checks without installing the game from `go/`:

```powershell
go vet ./... && go test ./...
```

[All docs](docs/README.md) · [Working agreement](AGENTS.md)
