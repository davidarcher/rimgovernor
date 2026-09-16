# Developer guide

[All docs](../README.md) · [Working agreement](../../AGENTS.md)

Start with the [architecture](architecture/overview.md) and
[source map](source-map.md), then read the contracts for the
component you will change. Keep work in small verified slices using the
[development workflow](development-process.md).

| Task | Start here |
| --- | --- |
| Set up locally | [Windows setup](../players/setup.md) |
| Run checks without game files | `go vet ./...` / `go test ./...` under `go/` (see [Go module](../../go/README.md)) |
| Change game behavior | [Subsystem contracts](contracts/README.md) |
| Change deployment names | [Project identity](project-identity.md) |
| Migrate an owned session | [Session migration](legacy-migration.md) |
| Work on the Go controller | [Go module](../../go/README.md) and [migration contracts](../../contracts/README.md) |
| Pick up unfinished work | [Backlog issues](https://github.com/davidarcher/rimgovernor/issues) |
| Choose which checks to run | [Testing pyramid and evidence rules](testing/choose-tests.md) |
| Run or add native acceptance | [go/internal/nativeaccept](../../go/internal/nativeaccept) harnesses, listed in [choose-tests.md](testing/choose-tests.md#available-checks) |

The old Python scenario/acceptance docs (`testing/first-check.md`,
`testing/README.md`, `docker-checks.md`, `headless-probes.md` and the rest of
that tree) were removed in
[G01.13](https://github.com/davidarcher/rimgovernor/issues/33) along with the
Python toolchain they documented. [testing/choose-tests.md](testing/choose-tests.md)
replaces them for the Go-only checks that exist today; see
[issue #38](https://github.com/davidarcher/rimgovernor/issues/38) for the
native acceptance migration's current slice. The multi-instance local colony
directory (`--colonies`) was removed the same way; see
[issue #47](https://github.com/davidarcher/rimgovernor/issues/47).

## Component guides

- [Control loop](architecture/control-loop.md): observations, priorities and scheduling.
- [Plans and Hands](architecture/plans-and-hands.md): admission, execution and completion.
- [Space and resources](architecture/space-and-resources.md): placement and shared budgets.
- [Sessions and recovery](architecture/sessions-and-recovery.md): authority, checkpoints and cleanup.
- [Dashboard](architecture/dashboard.md): presentation, video and input ownership.
- [World progression](architecture/world-progression.md): caravans, quests and world outcomes.
