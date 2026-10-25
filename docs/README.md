# Documentation

## Play

Start with the [player guide](players/README.md).

For recovered player outcomes and their implementation/evidence limits, see the
[product requirements](requirements/README.md): colony outcomes, representative
code and test evidence, and a prioritized review queue.

| Task | Guide |
| --- | --- |
| Install and prepare the game | [Setup](players/setup.md) |
| Start a save or generate a colony | [Launch](players/launch.md) |
| Understand the launcher and take control | [Controls](players/controls.md) |
| Save, stop and resume | [Save and resume](players/save-and-resume.md) |
| Tell the governor what you want: goals, themes and preferences | [Goals and themes](players/goals.md) |

## Develop

For dated native trader supply, stock waits and medicine purchases, see the [forecast contract](developers/contracts/forecast-contracts.md#stock-arrival-3868).
For dated raid medicine use, disease retends and stock-dependent recovery, see [scheduled medicine consumption](developers/contracts/forecast-contracts.md#scheduled-medicine-consumption).

Start with the [architecture](developers/architecture/overview.md) and
[source map](developers/source-map.md). The [developer guide](developers/README.md)
links the component guides; the [glossary](developers/glossary.md) defines terms.

| Task | Guide |
| --- | --- |
| Make and land a change | [Workflow](developers/development-process.md), [agent rules](../AGENTS.md) |
| Build, test and run the C# brain | [Development workflow](developers/development-process.md), [runbook](developers/agent-runbook.md) |
| Work safely on the shared machine | [Runbook](developers/agent-runbook.md) |
| Choose evidence for a change | [Choose tests](developers/testing/choose-tests.md) |
| Rules every native case follows | [Acceptance charter](developers/testing/acceptance-guide.md#charter) |
| Write or iterate on a native case | [Acceptance guide](developers/testing/acceptance-guide.md) |
| Run the in-process acceptance pilot and its failure exercises | [Rectangle pilot](developers/testing/acceptance-guide.md#in-process-rectangle-pilot) |
| Replay planner decisions | [Colony snapshots](developers/testing/colony-snapshots.md) |

| Read planning definitions and required native stats | [Stat and planning values](developers/contracts/stat-classes.md) |
| History: which native reads copied def data | [Static-data census (history)](developers/static-data-census.md) |
| History: why the Go brain was ported by hand rather than with go2cs | [Port spike (history)](developers/architecture/go-to-csharp-port-spike.md) |
| History: layout and idiom map of the Go-to-C# port | [Port conventions (history)](developers/architecture/go-to-csharp-port-conventions.md) |
| Call the native mod from the in-process brain (facade, frame handoff, def stat table) | [Game API](developers/architecture/game-api.md) |
| Own observed facts and requests across the native boundary | [Interface contracts](developers/contracts/interface-contracts.md), [record orphan gate](developers/architecture/rules.md#record-orphans) |
| Host the brain inside the mod (lifecycle, scheduler thread, watchdog, fault isolation) | [Brain host](developers/architecture/brain-host.md) |
| Check native arena lifetime and repeated hunting outcomes | [Real engine](../integrations/rimgovernor-realcombat/README.md#arena-lifetime-and-repeatability) |
| Request bounded hypothetical encounter estimates and interpret qualification | [Encounter prediction](developers/contracts/encounter-prediction.md) |
| Read the total colonist census and plan medical care and medicine reserves | [Medical care](developers/contracts/medical-care.md), [pawn work profiles](developers/contracts/work-assignment.md), [journal ownership](developers/contracts/persistence-contracts.md) |
| Read disaster service recovery, shelter lifecycle states and cold dash bounds | [Disaster planning](developers/contracts/disaster-planning.md) |
| Read combat targeting, raid behavior and weather waits | [Combat in the game](developers/architecture/combat-game-ai.md) |
| Read canonical planning cells and distinguish growing-room eligibility from native roofs | [Native planning cell ownership](developers/contracts/spatial-contracts.md#native-planning-cell-ownership) |
| Read the total room census and royalty housing requirements | [Room observations](developers/contracts/spatial-contracts.md#total-room-observations) |
| Trace a hunt from the native read through the roster comparison, the drafted formation and its release | [Hunt comparison](developers/contracts/forecast-contracts.md#coherent-coupled-observation-frame), [lone hunts and the hunt gate](developers/contracts/controller-contracts.md), [on-demand hunting evidence](developers/testing/acceptance-guide.md#tiers) |
| Reach the mod over local HTTP (endpoint file, host/origin checks, `/native/<tool>`, player routes) | [HTTP channel](developers/architecture/http-channel.md) |

| Compare selected forecasts with native outcomes | [Residual matching](developers/contracts/forecast-contracts.md#residual-matching), [native horizon evidence](developers/contracts/forecast-contracts.md#native-residual-evidence), [OTLP evidence and report](developers/contracts/otel-signals.md#forecast-residual-evidence) |
| Measure performance | [Throughput](developers/testing/measure-throughput.md), [hazard bounds](developers/architecture/hazard-detection-bounds.md) |
| Improve expert play | [Architecture roadmap](developers/architecture/expert-play-assessment.md) |
| Understand how policy chooses and advances a Method | [Domain flow](developers/architecture/overview.md#from-a-concern-to-work), [normal portfolio selection](developers/architecture/portfolio-selection.md), [selection and binding](developers/architecture/plans-and-hands.md#method-selection-and-binding), [live policy stages and enforcement](developers/architecture/colony-forecast.md#live-activity-policy-stages), [continuation](developers/contracts/method-continuation.md) |
| Forecast colony plans, total calendar and environment censuses, shared pawn time and bounded activity coordination | [ColonyForecast](developers/architecture/colony-forecast.md), [coupled food, needs and production](developers/architecture/colony-forecast.md#coupled-food-needs-and-production-trajectory), [coherent native forecast frame](developers/contracts/forecast-contracts.md#coherent-coupled-observation-frame), [competing outcomes and shared labor](developers/architecture/colony-forecast.md#competing-outcomes-and-shared-labor), [shelter and food selection](developers/architecture/colony-forecast.md#shelter-and-food-selection), [observed food candidates and batch sizes](developers/architecture/colony-forecast.md#food-candidates-from-acquisition-and-recipe-observations), [live ongoing-work protection](developers/contracts/work-assignment.md#ongoing-work-and-shared-occupancy) |
| Understand the tier-2 colony model (a game year of stocks, flows and projects) and the investment credit | [Tier 2 colony model](developers/architecture/colony-forecast.md#tier-2-colony-model), [tier-2 design and marginal value](developers/architecture/forecast-value.md#tier-2-event-driven-colony-model), [carried state and time](developers/contracts/forecast-contracts.md#carried-long-term-model-state), [`colony_model` event](developers/contracts/otel-signals.md#kinds), [why the colony starts long projects](players/goals.md#long-projects-when-food-is-secure) |
| Account for material promises and exclusive equipment | [Forecast claims](developers/architecture/colony-forecast.md#material-promises-and-exclusive-equipment), [live fixed batches](developers/architecture/colony-forecast.md#live-fixed-batch-material-promises), [unfinished work](developers/architecture/colony-forecast.md#ingredients-inside-unfinished-work), [atomic batch admission](developers/architecture/colony-forecast.md#fixed-batch-material-admission), [joint material choices](developers/architecture/colony-forecast.md#joint-production-material-choices), [need valuation proof](developers/architecture/colony-forecast.md#need-based-production-valuation-proof), [live clothing recipe alternatives and acceptance](developers/contracts/equipment-upkeep.md#live-clothing-requirement-selection) |
| Read the value currency and raid harm model (implemented, three calibrated exposure cells) and review the proposed long horizon | [Forecast value](developers/architecture/forecast-value.md), [joiner situational traits](developers/contracts/forecast-contracts.md#joiner-situational-traits), [raid exposure calibration](developers/testing/harm-exposure-calibration.md) |
| Read native storyteller spacing, scheduled incident times and forecast coverage | [Native incident occurrence windows](developers/contracts/forecast-contracts.md#native-incident-occurrence-windows) |
| Read dining and recreation memories, native joy-session limits and modded memory stacking | [Room credits](developers/architecture/forecast-value.md), [native forecast facts](developers/contracts/forecast-contracts.md) |
| Price prison recruitment and conversion, bedless-memory renewal, cumulative chat memories and native projected warden round trips | [Prison credit](developers/architecture/forecast-value.md), [native prison reads](developers/contracts/forecast-contracts.md) |
| Read nursery crib rest, heard-fit, sleeping-hearer disturbance, opinion-to-mood feedback and caretaker play credits, including exposure assumptions | [Nursery credit](developers/architecture/forecast-value.md), [native sleeping and baby reads](developers/contracts/forecast-contracts.md), [exact service positions and native choice bounds](developers/contracts/forecast-contracts.md#service-positions), [native baby care occupancy](developers/contracts/forecast-contracts.md#baby-care-occupancy) |
| Research predictive combat and tactical epics | [Combat predictability paper](developers/architecture/combat-predictability-research.md) |
| Understand how player goals become Needs and how the in-game window reads them | [Player goals](developers/architecture/player-goals.md), [goals window](developers/architecture/in-game-panel.md), [goals contracts](developers/contracts/goals-contracts.md) |
| Plan colony rooms and containment | [Facilities](developers/architecture/facilities.md) |
| Capture, rescue and admit population | [Population commitments](developers/contracts/population-contracts.md) |
| Understand territory and Home margins | [Colony extent](developers/contracts/colony-extent.md), [Home coverage](developers/contracts/upkeep-contracts.md#home-coverage) |
| Change a subsystem | [Behavior contracts](developers/contracts/README.md) |
| Assess autonomous development shortfalls and bounded resilience | [Essential services](developers/contracts/essential-services.md) |
| See which autonomous-development claims are observed in the real game and which only in pure tests | [Observed evidence](developers/contracts/essential-services.md#observed-evidence-and-remaining-gaps) |
| Understand useful cooking development and its limits | [Productive skill learning](developers/contracts/skill-learning.md#productive-cooking-in-normal-selection) |
| Judge whether game mechanics can be extracted into cards (Gate 0 spike, #3218) and read the shipped cards | [Mechanic-card spike](developers/mechanic-card-spike.md), [mechanic cards](developers/contracts/mood-control.md#mechanic-cards) |
| Select and execute comms, trader visits and settlement missions with cheapest-carried medicine purchases | [Controllable trade](developers/contracts/controllable-trade.md), [stock arrival per outcome](developers/contracts/forecast-contracts.md#stock-arrival-3868) |
| Read conditional major-threat rates and native biome incident eligibility | [Threat schedule](developers/contracts/forecast-contracts.md#threat-schedule-3539) |
| Preserve or reconsider issued work | [Method continuation](developers/contracts/method-continuation.md) |
| Add or change a Family with Needs and claimed Methods | [Model](developers/architecture/control-loop.md#target-model-needs-families-and-claimed-methods), [family notes and map](developers/architecture/control-loop.md#migration) |
| Trace observed harvest through cooking and meal delivery | [Live food Method](developers/contracts/pawn-activities.md#live-one-iteration-cooking) |
| Trace shared helper coordination through supply and finishing | [Live construction Method](developers/contracts/pawn-activities.md#live-material-delivery-and-building) |
| Choose care, demand-driven deliveries and useful chunk work | [Support work](developers/contracts/support-work.md), [case disposition](developers/contracts/support-work.md#execution-and-evidence) |
| Understand ordinary pawn ownership and unsupported voluntary services | [Voluntary control](developers/contracts/voluntary-control.md), [acceptance disposition](developers/contracts/voluntary-control.md#acceptance-disposition) |
| Change the native boundary | [Wire contracts](../contracts/README.md) |
| Measure coupled portfolio allocations and bounded selection quality | [Portfolio allocation baseline](developers/testing/portfolio-allocations.md) |

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

- [Native skill learning and decay](developers/contracts/skill-learning.md): recipe/range/sparring learning, native decay, and childhood work value within the colony horizon.

- [Hunting prediction qualification](developers/testing/hunting-prediction-qualification.md): independent native CombatLab trials, the 15-cell hunter-set grid, ranking and harm calibration scoring.
- [Raid exposure calibration](developers/testing/harm-exposure-calibration.md): the combat sweep's per-colonist rows, ratio bins, the exposure table and its reader.
- [Assigned capability maintenance](developers/contracts/capability-maintenance.md): actual role requirements, native skill decay and selected practice.

Food/shelter investment and unified construction adoption: [essential-service contract](developers/contracts/essential-services.md#autonomous-food-and-shelter-investment).

- [Clock states and recovery](developers/contracts/clock-recovery.md) and [receipt evidence](developers/contracts/action-contracts.md): complete native reads and named operation evidence.
- [Equipment census](developers/contracts/equipment-upkeep.md#native-observations-and-selection): same-frame gear, growing fields and pawn roles on routine and direct colony reads.
- [Failed snapshot reads](developers/contracts/clock-recovery.md#failed-snapshot-reads): explicit building-read failures, preserved diagnostics, finished frame labor and same-tick recovery.
- [Acceptance harness lifecycle](developers/testing/acceptance-guide.md): warm games and request-based idle cleanup.

- [Food storage, refrigeration and freezer value](developers/contracts/food-storage.md): food censuses, reserve reads and cooling geometry.

Workshop and laboratory productivity: [bench room credits](developers/contracts/forecast-contracts.md#workshop-and-laboratory-bench-credits).

Storyteller generator points and raid harm mixtures: [threat schedule contract](developers/contracts/forecast-contracts.md#threat-schedule-3539).
Disease incident timing and conditional future comp inputs: [illness forecast](developers/contracts/forecast-contracts.md#illness-term-3824).
Storyteller generator points and raid harm mixtures: [threat schedule contract](developers/contracts/forecast-contracts.md#threat-schedule-3539).
