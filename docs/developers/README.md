# Developer guide

[All docs](../README.md) · [Working agreement](../../AGENTS.md)

Start with the [architecture](architecture/overview.md) and
[source map](source-map.md), then read the contracts for the
component you will change. Keep work in small verified slices using the
[development workflow](development-process.md).

| Task | Start here |
| --- | --- |
| Set up locally | [Windows setup](../players/setup.md) |
| Run checks without game files | [First Docker check](testing/first-check.md) |
| Choose tests or run a native scenario | [Testing](testing/README.md) |
| Change game behavior | [Subsystem contracts](contracts/README.md) |
| Change deployment names | [Project identity](project-identity.md) |
| Browse running Docker colonies | [Local colony directory](local-colonies.md) |
| Migrate an owned session | [Session migration](legacy-migration.md) |
| Work on the Go controller | [Go module](../../go/README.md) and [migration contracts](../../contracts/README.md) |
| Pick up unfinished work | [Backlog](../BACKLOG.md) |

## Component guides

- [Control loop](architecture/control-loop.md): observations, priorities and scheduling.
- [Plans and Hands](architecture/plans-and-hands.md): admission, execution and completion.
- [Space and resources](architecture/space-and-resources.md): placement and shared budgets.
- [Sessions and recovery](architecture/sessions-and-recovery.md): authority, checkpoints and cleanup.
- [Dashboard](architecture/dashboard.md): presentation, video and input ownership.
- [World progression](architecture/world-progression.md): caravans, quests and world outcomes.
