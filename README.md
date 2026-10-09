# RimGovernor

A local RimWorld colony controller. Deterministic policy manages colony needs;
RimWorld applies its normal rules and runs the simulation. Routine play needs
no language model.

## Play

Double-click `RimGovernor.cmd`, then press **Play**. The launcher builds the
controller and native mod and starts automatic control. Pause in RimWorld to take
manual control.

A licensed RimWorld installation is required. Start with
[setup](docs/players/setup.md), then the [player guide](docs/players/README.md)
for launch options, controls and saving.

## Develop

The controller and launcher are Go; the game host and native bridge are C#.
Start with the [architecture](docs/developers/architecture/overview.md) and
[source map](docs/developers/source-map.md).

From `go/`, the development check needs no game installation:

```powershell
go run ./cmd/test > ../test.out 2>&1
```

See the [workflow](docs/developers/development-process.md) for landing changes
and [choose-tests](docs/developers/testing/choose-tests.md) for additional checks.

[Documentation](docs/README.md) · [Agent rules](AGENTS.md) ·
[Backlog](https://github.com/davidarcher/rimgovernor/issues)
