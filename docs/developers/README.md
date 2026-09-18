# Developer guide

[All docs](../README.md) · [Working agreement](../../AGENTS.md)

Start with the [architecture](architecture/overview.md) and
[source map](source-map.md), then read the contracts for the
component you will change. Keep work in small verified slices using the
[development workflow](development-process.md).

| Task | Start here |
| --- | --- |
| Set up locally | [Windows setup](../players/setup.md) |
| Work beside other agent sessions on one machine | [Agent runbook](agent-runbook.md) |
| Run checks without game files | `go run ./cmd/test` under `go/` (see [Go module](../../go/README.md)) |
| Change game behavior | [Subsystem contracts](contracts/README.md) |
| Change deployment names | [Project identity](project-identity.md) |
| Work on the Go controller | [Go module](../../go/README.md) and [wire contracts](../../contracts/README.md) |
| Pick up unfinished work | [Backlog issues](https://github.com/davidarcher/rimgovernor/issues) |
| Choose which checks to run | [Testing pyramid and evidence rules](testing/choose-tests.md) |
| Measure controller throughput or find where bridge time goes | [Measure throughput](testing/measure-throughput.md) |
| Run or add native acceptance | [go/internal/nativeaccept](../../go/internal/nativeaccept) cases through `cmd/acceptance`; [choose-tests.md](testing/choose-tests.md#adding-a-case) says how to add one and lists the [available checks](testing/choose-tests.md#available-checks) |

[testing/choose-tests.md](testing/choose-tests.md) covers the checks that exist
today; [issue #38](https://github.com/davidarcher/rimgovernor/issues/38) tracks
native acceptance coverage and [issue #47](https://github.com/davidarcher/rimgovernor/issues/47)
the multi-instance colony directory.

## Component guides

- [Control loop](architecture/control-loop.md): observations, priorities and scheduling.
- [Plans and Hands](architecture/plans-and-hands.md): admission, execution and completion.
- [Space and resources](architecture/space-and-resources.md): placement and shared budgets.
- [Facilities](architecture/facilities.md): the room-function ladder and per-role matrix.
- [Sessions and recovery](architecture/sessions-and-recovery.md): authority, checkpoints and cleanup.
- [Dashboard](architecture/dashboard.md): presentation, video and input ownership.
- [World progression](architecture/world-progression.md): caravans, quests and world outcomes.
