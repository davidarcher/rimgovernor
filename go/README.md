# Go controller

[Developer guide](../docs/developers/README.md) ·
[Source map](../docs/developers/source-map.md)

This module contains the controller, launcher and acceptance runner. The
controller observes RimWorld through GABP, evaluates deterministic policy,
executes admitted actions and serves the launcher API.

## Build and check

Use the toolchain in [.go-version](.go-version). From this directory:

```powershell
go run ./cmd/test > ../test.out 2>&1
go build -o ../.rimgovernor/bin/rimgovernor.exe ./cmd/rimgovernor
```

The check command owns formatting, analysis and short tests. See
[choose-tests](../docs/developers/testing/choose-tests.md) for full tests,
race checks and C#/protobuf gates. Tests using fakes or recorded observations
need no game installation.

[Wire generation](../contracts/schema-generation.md) updates Go and C# together.
Generated decoding preserves presence; domain validation enforces bounds,
scope and authority.

## Running

For normal play, use `RimGovernor.cmd` at the repository root. To build the
Windows launcher explicitly:

```powershell
go build -ldflags -H=windowsgui -o ../RimGovernorLauncher.exe ./cmd/launcher
```

The launcher rebuilds stale controller/native binaries and starts `serve`
with settings from `.rimgovernor/launcher.json`. It passes `--resume`, enabling
automatic control at startup and after loads.

Use `rimgovernor serve -h` for the complete flag list.

| Mode or option | Purpose |
| --- | --- |
| `serve --profile <path>` | Autonomous play with a private game profile. |
| `serve --observe` | Read-only observation; no control authority or writes. |
| `--config`, `--game` | Game configuration directory and configured game ID. |
| `--state` | SQLite session journal path; not a saved colony timeline. |
| `--listen` | Loopback API address; the default chooses a free port. |
| `--resume` | Enable control at startup and after each load; bare serve otherwise waits for Resume. |
| `--follow-player-speed` | Follow the player's speed choice instead of the default Ultrafast policy. |
| `--flight-recorder` | Choose the always-on flight recorder's path. |

Colony intent is saved in RimWorld's `GovernorState` blobs. SQLite records the
controller session; it is not a database to pair manually with an ordinary
game save. See [persistence](../docs/developers/contracts/persistence-contracts.md).

## Find the implementation

| Work | Owner |
| --- | --- |
| Types and identities | [internal/domain](internal/domain) |
| Pure decisions and forecasts | [internal/policy](internal/policy) |
| Planner scheduling and composition | [internal/buildingruntime](internal/buildingruntime) |
| Admission and session journal | [internal/store](internal/store) |
| Native dispatch and reconciliation | [internal/executor](internal/executor) |
| GABP and typed observations | [internal/bridge](internal/bridge), [internal/observation](internal/observation) |
| Player API | [internal/httpapi](internal/httpapi); [contract](../docs/developers/contracts/go-player-api.md) |
| Launcher | [cmd/launcher](cmd/launcher); [guide](../docs/developers/architecture/launcher.md) |
| Offline supply scenarios | [internal/supplysim](internal/supplysim); [supply model](../docs/developers/architecture/supply-model.md) |
| Native acceptance | [internal/nativeaccept](internal/nativeaccept); [case guide](../docs/developers/testing/acceptance-guide.md) |

Read the [control loop](../docs/developers/architecture/control-loop.md) for
execution flow and [subsystem contracts](../docs/developers/contracts/README.md)
for individual gameplay rules. These are the authoritative references rather
than a second per-family inventory here.

## Diagnostics and replay

Start with `flight.jsonl`; `rimgovernor log`, `phases` and `trace` read its
segments. See [measure throughput](../docs/developers/testing/measure-throughput.md)
for fields and examples. A process's stderr contains startup/fatal information,
not the gameplay decision history.

[Colony snapshots](../docs/developers/testing/colony-snapshots.md) replay planner
decisions without the game. Optional native-capture tests document their
`RIMGOVERNOR_NATIVE_*` inputs beside the test that consumes them; find them with
`rg 'RIMGOVERNOR_NATIVE_' internal`. Replaying a capture proves decoding or
decision behavior, not a new live-game outcome.
