# Developer guide

[Documentation](../README.md) · [Agent rules](../../AGENTS.md)

Read the [architecture](architecture/overview.md), find the owner in the
[source map](source-map.md), then open the affected
[contract](contracts/README.md). Use the [workflow](development-process.md) to
verify and land the change.

## Start working

| Task | Reference |
| --- | --- |
| Local machine and private game setup | [Runbook](agent-runbook.md) |
| Go build and execution | [Module guide](../../go/README.md) |
| Test selection | [Choose tests](testing/choose-tests.md) |
| Native case authoring and iteration | [Acceptance guide](testing/acceptance-guide.md) |
| Planner replay | [Colony snapshots](testing/colony-snapshots.md) |
| Performance diagnosis | [Measure throughput](testing/measure-throughput.md) |
| Schema changes | [Wire contracts](../../contracts/README.md) and [generation](../../contracts/schema-generation.md) |
| Deployment names | [Project identity](project-identity.md) |
| Architectural constraints | [Rules](architecture/rules.md) |
| Terms | [Glossary](glossary.md) |

## Component guides

| Guide | Responsibility |
| --- | --- |
| [Control loop](architecture/control-loop.md) | Rounds, planner scheduling, events and Manual control |
| [Plans and Hands](architecture/plans-and-hands.md) | Admission, dispatch, receipts and outcomes |
| [Sessions and recovery](architecture/sessions-and-recovery.md) | Authority, save/load, reconnect and uncertainty |
| [Space and resources](architecture/space-and-resources.md) | Placement and material accounting |
| [Supply model](architecture/supply-model.md) | Demand, acquisition and forecasts |
| [Facilities](architecture/facilities.md) | Room functions and construction prerequisites |
| [Storage](architecture/storage.md) | Department-owned stockpiles |
| [Combat](architecture/combat-game-ai.md) | Native combat behavior and Go tactics |
| [Hazard bounds](architecture/hazard-detection-bounds.md) | Detection cadence and clock stops |
| [Launcher](architecture/launcher.md) | UI, session controls and serve client |
| [World progression](architecture/world-progression.md) | Caravans, quests and world outcomes |

The [expert-play roadmap](architecture/expert-play-assessment.md) defines proposed
stronger guarantees and the evidence needed to claim improvement. Open work lives
in the [backlog](https://github.com/davidarcher/rimgovernor/issues).
