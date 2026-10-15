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
| History: layout and idiom map of the Go-to-C# port | [Port conventions (history)](developers/architecture/go-to-csharp-port-conventions.md) |
| Call the native mod from the in-process brain (facade, frame handoff, game-thread stat service and its time budget) | [Native facade](developers/architecture/native-facade.md) |
| Host the brain inside the mod (lifecycle, scheduler thread, watchdog, fault isolation) | [Brain host](developers/architecture/brain-host.md) |
| Check native arena lifetime and repeated hunting outcomes | [Real engine](../integrations/rimgovernor-realcombat/README.md#arena-lifetime-and-repeatability) |
| Request bounded hypothetical encounter estimates and interpret qualification | [Encounter prediction](developers/contracts/encounter-prediction.md) |
| Reach the mod over local HTTP (endpoint file, host/origin checks, `/native/<tool>`, player routes) | [HTTP channel](developers/architecture/http-channel.md) |

| Measure performance | [Throughput](developers/testing/measure-throughput.md), [hazard bounds](developers/architecture/hazard-detection-bounds.md) |
| Improve expert play | [Architecture roadmap](developers/architecture/expert-play-assessment.md) |
| Understand how policy chooses and advances a Method | [Domain flow](developers/architecture/overview.md#from-a-concern-to-work), [normal portfolio selection](developers/architecture/portfolio-selection.md), [selection and binding](developers/architecture/plans-and-hands.md#method-selection-and-binding), [live policy stages and enforcement](developers/architecture/colony-forecast.md#live-activity-policy-stages), [continuation](developers/contracts/method-continuation.md) |
| Forecast colony plans, shared pawn time and bounded activity coordination | [ColonyForecast](developers/architecture/colony-forecast.md), [coupled food, needs and production](developers/architecture/colony-forecast.md#coupled-food-needs-and-production-trajectory), [coherent native forecast frame](developers/contracts/forecast-contracts.md#coherent-coupled-observation-frame), [competing outcomes and shared labor](developers/architecture/colony-forecast.md#competing-outcomes-and-shared-labor), [observed survival deadlines](developers/architecture/colony-forecast.md#observed-deadlines-for-shelter-and-food), [candidate effects](developers/architecture/colony-forecast.md#quantitative-activity-effects), [observed food candidates and batch sizes](developers/architecture/colony-forecast.md#food-candidates-from-acquisition-and-recipe-observations), [live ongoing-work protection](developers/contracts/work-assignment.md#ongoing-work-and-shared-occupancy) |
| Account for material promises and exclusive equipment | [Forecast claims](developers/architecture/colony-forecast.md#material-promises-and-exclusive-equipment), [live fixed batches](developers/architecture/colony-forecast.md#live-fixed-batch-material-promises), [unfinished work](developers/architecture/colony-forecast.md#ingredients-inside-unfinished-work), [atomic batch admission](developers/architecture/colony-forecast.md#fixed-batch-material-admission), [joint material choices](developers/architecture/colony-forecast.md#joint-production-material-choices), [need valuation proof](developers/architecture/colony-forecast.md#need-based-production-valuation-proof), [live clothing recipe alternatives and acceptance](developers/contracts/equipment-upkeep.md#live-clothing-requirement-selection) |
| Research predictive combat and tactical epics | [Combat predictability paper](developers/architecture/combat-predictability-research.md) |
| Plan colony rooms and containment | [Facilities](developers/architecture/facilities.md) |
| Understand territory and Home margins | [Colony extent](developers/contracts/colony-extent.md), [Home coverage](developers/contracts/upkeep-contracts.md#home-coverage) |
| Change a subsystem | [Behavior contracts](developers/contracts/README.md) |
| Assess autonomous development shortfalls and bounded resilience | [Essential services](developers/contracts/essential-services.md) |
| Plan and reconcile trade missions | [Controllable trade](developers/contracts/controllable-trade.md) |
| Preserve or reconsider issued work | [Method continuation](developers/contracts/method-continuation.md) |
| Trace observed harvest through cooking and meal delivery | [Live food Method](developers/contracts/pawn-activities.md#live-one-iteration-cooking) |
| Trace shared helper coordination through supply and finishing | [Live construction Method](developers/contracts/pawn-activities.md#live-material-delivery-and-building) |
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

- [Pawn activity observations and reports](developers/architecture/pawn-activity.md)

- [Planner-owned pawn activities](developers/contracts/pawn-activities.md): [live harvest and delivery](developers/contracts/pawn-activities.md#live-harvest-and-delivery), Method admission, [continuation and release](developers/contracts/method-continuation.md#pawn-activity-methods), exact cooking ingredients and delivery destinations, needs execution, bounded holds and ownership.

The [live work-resource view](developers/architecture/colony-forecast.md#shared-live-work-resources)
owns the shared observation and journal projection for migrated work paths;
[work assignment](developers/contracts/work-assignment.md#shared-method-availability)
and [persistence](developers/contracts/persistence-contracts.md) define its consumers and state ownership.

- [Needs alternatives and future capacity](developers/architecture/colony-forecast.md#needs-alternatives-and-future-capacity): ordinary-review candidates, shared occupancy, conditional mechanics, native residual scene and supported states.

- [Native skill learning and decay](developers/contracts/skill-learning.md): recipe/range/sparring learning paths, native decay and conditional capability projection.
