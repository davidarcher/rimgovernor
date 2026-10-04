# RimGovernor

A local RimWorld colony controller. Autopilot handles routine colony needs and
routine player control. The launcher shows what the colony is doing and what
is going wrong while RimWorld runs the simulation.

## Play

Double-click `RimGovernor.cmd` and press Play (see [setup](docs/players/setup.md)).

The controller starts paused; choose Resume in the launcher to enable routine
control. Autopilot needs no model.

This is a development setup requiring licensed RimWorld files, native mods
and a prepared save. See the [player guide](docs/players/README.md) for
controls, saving and troubleshooting. Open work is in
[GitHub issues](https://github.com/davidarcher/rimgovernor/issues).

## Develop

Use the [developer guide](docs/developers/README.md) to find the architecture,
source and checks for your change. Go runs the production controller
(started by `RimGovernorLauncher.exe`, built from `go/cmd/launcher`, which is also the player UI), and C#
supplies native game tools through RimBridgeServer over GABP. See
[the Go module guide](go/README.md) for building, running and testing it.

You can run checks without installing the game from `go/`:

```powershell
go vet ./... && go test ./...
```

[All docs](docs/README.md) · [Working agreement](AGENTS.md)
