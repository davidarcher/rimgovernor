# Documentation

## Play

Start with the [player guide](players/README.md).

| Task | Guide |
| --- | --- |
| Install and prepare the game | [Setup](players/setup.md) |
| Start a save or generate a colony | [Launch](players/launch.md) |
| Understand the launcher and take control | [Controls](players/controls.md) |
| Save, stop and resume | [Save and resume](players/save-and-resume.md) |

## Develop

Start with the [architecture](developers/architecture/overview.md) and
[source map](developers/source-map.md). The [developer guide](developers/README.md)
links the component guides; the [glossary](developers/glossary.md) defines terms.

| Task | Guide |
| --- | --- |
| Make and land a change | [Workflow](developers/development-process.md), [agent rules](../AGENTS.md) |
| Build, test and run the C# brain | [Development workflow](developers/development-process.md), [runbook](developers/agent-runbook.md) |
| Work safely on the shared machine | [Runbook](developers/agent-runbook.md) |
| Choose evidence for a change | [Choose tests](developers/testing/choose-tests.md) |
| Write or iterate on a native case | [Acceptance guide](developers/testing/acceptance-guide.md) |
| Replay planner decisions | [Colony snapshots](developers/testing/colony-snapshots.md) |

| History: which native reads copied def data | [Static-data census (history)](developers/static-data-census.md) |
| History: why the Go brain was ported by hand rather than with go2cs | [Port spike (history)](developers/architecture/go-to-csharp-port-spike.md) |
| Judge whether the in-process C# brain can reload in a running game | [Brain hot reload feasibility](developers/architecture/brain-hot-reload-feasibility.md) |
| History: layout and idiom map of the Go-to-C# port | [Port conventions (history)](developers/architecture/go-to-csharp-port-conventions.md) |
| Call the native mod from the in-process brain (facade, frame handoff, game-thread stat service and its time budget) | [Native facade](developers/architecture/native-facade.md) |
| Host the brain inside the mod (lifecycle, scheduler thread, watchdog, fault isolation) | [Brain host](developers/architecture/brain-host.md) |
| Reach the mod over local HTTP (endpoint file, host/origin checks, `/native/<tool>`, player routes) | [HTTP channel](developers/architecture/http-channel.md) |

| Measure performance | [Throughput](developers/testing/measure-throughput.md), [hazard bounds](developers/architecture/hazard-detection-bounds.md) |
| Improve expert play | [Architecture roadmap](developers/architecture/expert-play-assessment.md) |
| Forecast colony plans and inventory consequences | [ColonyForecast](developers/architecture/colony-forecast.md) |
| Research predictive combat and tactical epics | [Combat predictability paper](developers/architecture/combat-predictability-research.md) |
| Plan colony rooms and containment | [Facilities](developers/architecture/facilities.md) |
| Understand territory and Home margins | [Colony extent](developers/contracts/colony-extent.md), [Home coverage](developers/contracts/upkeep-contracts.md#home-coverage) |
| Change a subsystem | [Behavior contracts](developers/contracts/README.md) |
| Plan and reconcile trade missions | [Controllable trade](developers/contracts/controllable-trade.md) |
| Preserve or reconsider issued work | [Method continuation](developers/contracts/method-continuation.md) |
| Change the native boundary | [Wire contracts](../contracts/README.md) |

## Gameplay and remote evidence

- [Shelter coverage](developers/testing/shelter-coverage.md): which check proves each claim.
- [Colony review](developers/testing/colony-review.md): nightly colony runs and screenshots.
- [Remote handoff](developers/testing/remote-handoff.md): prepare, dispatch, diagnose and import.
- [Remote Windows workflow](developers/testing/remote-workflow.md) and
  [encrypted bundles](developers/remote-bundles.md): runner setup and artifacts.
- [Remote acceptance contract](developers/contracts/remote-acceptance.md) and
  [evidence aggregation](developers/testing/remote-evidence.md): manifests and verdicts.

[GitHub issues](https://github.com/davidarcher/rimgovernor/issues) hold unfinished
work and decisions. Documentation describes current behavior and explicit
proposals; Git and result artifacts retain implementation history and run evidence.
