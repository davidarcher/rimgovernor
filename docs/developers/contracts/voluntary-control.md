# Voluntary control

Ordinary controlled-pawn work belongs to the shared Concern/Method path:
native observations → candidate generation → ColonyForecast comparison →
Method admission and journal → Hands → observed completion and revision.
A proposal rejected by shared resource admission remains uncommitted and is reported as deferred; existing Methods keep authority and continue. An accepted intent is not completed work. Neither a timetable nor an adviser
may choose a competing ordinary job for a controlled pawn.

The [pawn activity contract](pawn-activities.md) owns exact execution and the
[continuation contract](method-continuation.md) owns release after the originating
Concern completes. [Portfolio selection](../architecture/portfolio-selection.md) owns comparison;
[support work](support-work.md) owns useful cleaning and demand-driven deliveries.
No additional scheduler, material ledger or persistence owner is introduced.

Selected input obligations retain their producer task identities in the Method
journal. Shared material accounting reserves the physical input frontier: a
pending delivery protects its quantity once its producer completes, including
while its pawn waits for another prerequisite. Downstream handoffs do not charge
the same units again; independent consuming branches do. Observed carried stock
is excluded from loose-stock claims. Completing a producer never credits its
predicted output to inventory; fresh native stock still determines availability.
Direct eating participates in this same material budget.
Each need trajectory is integrated only at events that affect that need. Unrelated service timing must not introduce numerical differences into higher-priority consequences; sleep activation still accounts for observed food recovery.
Observed need targets have the same whole-Method ownership during comparison
and admission: two participants cannot share an exact meal stack, bed or
recreation target, even when a proposed schedule places their use at different
times. Repeated use by the same participant remains possible.
Consequently, unclaimed units in a stack can remain inaccessible to another pawn
while a consuming Method owns future uses of that stack. Safe reconsideration of
those obligations and scarce shared-lot redistribution are tracked in [#3194](https://github.com/davidarcher/rimgovernor/issues/3194).
The repeated-needs case supplies independent physical lots; it does not prove redistribution.

## Reconsidering observed work

Current work is not automatically excluded from comparison. An exactly observed
stationary horseshoes service can be represented as retained work in the same
portfolio search. Its remaining native toil time and current gain rate supply a
bounded projection; changing tolerance and early native completion remain explicit
uncertainties. Sunk progress is already present in current need levels.

Reconsideration supports an observed single stationary service, or one exact
future sleep/recreation task whose only remaining participant is positively
observed preparing and waiting. Retaining future work includes the occupied wait,
fresh observed travel, and service through its original deadline; the comparator
cannot insert a second owner into that wait. Completed participant releases need
positive native ownership evidence. Carried/input/output obligations, mixed work,
active multi-task services and unknown route or service estimates remain protected.
The conditional comparison removes only that exact incumbent's claims; it does not
release native reservations or change the journal's admission view. True ties
retain work. A winning replacement first journals an exact revisioned release;
replacement admission waits for a fresh observation. A forecast of future work
after retention does not authorize a second owner for the same pawn.

## Control boundary and unsupported states

The native selection boundary distinguishes controlled voluntary work from
simulation and protection. Inactive authority and uncontrolled pawns retain
native selection. Downing, mental state, drafting, burning, forced player work
and queued orders are protection boundaries, not spare labor. Native exhaustion sleep and
the exact physiological Vomit job interrupt and release the voluntary assignment with
observed failure evidence. Native physiology,
legality, reservation checks and completion mechanics still run.

The shared selector may retain no change only for an observed, finite safe
interval. A missing estimate is not evidence that indefinite idleness is safe.
An unassigned controlled pawn exposes `unassigned_voluntary_pawn` through the
Supervisor rather than silently borrowing vanilla job selection. Unsupported
need/state observations are excluded from ordinary capacity and defer admission.
This is a visible unsupported outcome, not successful autonomous coverage.

The native support check admits the exact need types Food, Rest, Joy, Mood,
Comfort, Beauty, Outdoors, Indoors and RoomSize. Derived/modded classes do not
inherit that claim. An inactive `Need_Chemical_Any` hidden by the native
`ShowOnNeedList` rule is exempt; a visible chemical need is not. Psylink, active
hemogen/dependency genes and non-removed chemical-dependency hediffs add their
specific blockers. A scheduled drug produces either `scheduled_drug_due` or
`scheduled_drug_onset_unassessed`; a missing drug definition produces
`unknown_scheduled_drug`. Supported ordinary needs do not cancel another blocker.

Bounded `Idle` is selected only for an eligible pawn with known available
capacity, no selected task/claim and known needs above their thresholds. Its
deadline is at most 600 ticks and no later than the earliest forecast threshold
crossing or available-window end. Truncated observations or unknown need
estimates do not authorize this defer. Review must renew or replace it; missing
plans are visible unavailable outcomes, not successful endless holds. A completed or failed
assignment keeps its exact ownership pending continuation or release, but stops the existing
Supervisor clock; readmitting time cannot turn that terminal assignment into indefinite idle.

| Domain | Owner and present boundary |
| --- | --- |
| Food, rest, recreation | Shared candidates and forecast; exact ingestion, bed and horseshoes execution through Hands. Other recreation mechanisms need observed candidates and exact adapters before they become planner choices. |
| Harvest, food conversion, material delivery, construction, roof work, clothing | Shared task composition and selection, with observed input/output rebinding. A delivered garment is not evidence it was worn or that physiological cold exposure improved. |
| Cleaning and medical support | Shared support candidates; exact pending service identities and native conditional room factors. Critical medical protection remains with its existing emergency owner. No fabricated prognosis or retroactive post-tend infection benefit. |
| Fire/combat/player protection | Existing protection and emergency paths retain authority; an ordinary Method cannot convert protected occupancy into free capacity. |
| Drugs, addictions, genetic dependencies, hemogen, psyfocus, unsupported or modded needs | Native observations expose blockers. Ordinary safe-idle admission cannot cover them. A future Method must supply onset/effect observations and exact execution under the existing Concern owner before support can be claimed. |
| Mining, tree cutting, research, general repair, non-supported production and colony services | Existing provisioning/Concern mechanisms can describe demand or configure the world; this alone does not establish planner-owned pawn execution. Each supported service needs an exact shared Method and observable completion. Do not silently restore vanilla work selection for these gaps. |

The boundary does not establish universal autonomous play across DLCs and mods.
Hunting prediction/qualification and the hunting integration acceptance remain
their own gates; generic delivery support does not satisfy them.

## Provisioning versus executable selection

`GenerateCoupledActivities`, the shared outcome comparator and
`StartCoupledActivity` are the ordinary executable selection path.
`ContinueCoordinatedActivity` advances its selected obligations from fresh
observations. Per-family candidate providers retain alternatives; they must not
publish a private winning assignment before shared comparison.

The repository still has independently used provisioning calculations:

- `Rounder.ReviewMeals` calls `ReviewMealTier` to derive cooking readiness;
  `RoundsPaste` and `PasteSite` also use meal-tier review for dispenser planning.
- `RoundsFoodPlan` calls `SupplyFoodPlan`; `RoundsResourceSupply` calls
  `PlanResourceSupply`. Both use `PlanSupply` for resource channels including
  farming, mining, chopping, trade and bills.

These consumers prevent blanket deletion of MealTier or SupplyPlan. They are
not a second authorization to issue ordinary pawn jobs. Their admitted bills,
designations and resource commitments can still constrain the alternatives
visible to the executable selector. Removing that upstream ranking requires
replacing those actual consumers under their existing demand owners; renaming
the calculators or ignoring their commitments is not a cutover.

## Acceptance disposition

The following inventory separates native execution evidence from policy
comparison tests. “Keep” preserves the stated native claim; it does not claim a
current passing gameplay run. Registered cases and successful compilation do
not satisfy #3135's actual-gameplay gate.

| Exact case | Disposition and retained claim |
| --- | --- |
| `production/coupled-needs` | Rewrite/extend for normal shared selection through real ingestion, rest/wake and recreation, revision and release; preserve a legacy Work timetable as an adversarial fixture, not an expected policy. |
| `coordination/food-delegated` | Keep real harvest/output/delivery and participant ownership; compare alternatives in pure tests rather than asserting a mandatory helper tactic for every world. |
| `coordination/food-carried-recovery` | Keep interrupted carry, exact remaining quantity and observed recovery/release. |
| `coordination/construction-delegated` | Keep actual material transfer, frame rebinding and building completion. Thermal benefit requires separate observed exposure evidence. |
| `production/clothing-activity` | Keep normal selected-stuff production and input conservation; do not equate production with wearing. |
| `production/clothing-activity-unfinished` | Keep observed unfinished-item resumption and conservation, without spending loose inputs twice. |
| `clean/bench` | Keep shared useful cleaning plus real production and native room improvement. |
| `clean/care` | Keep urgent treatment under the critical owner, with separately useful shared cleanup; cleaning must not delay urgent care. |
| `clean/care-routine` | Keep routine shared exact tend and useful cleaning; no legacy direct-tend substitution. |
| `storage/refill-hysteresis` | Keep rewritten exact demand-driven quantity and actual delivery; the old refill threshold is not an assertion. |
| `storage/changed-demand` | Keep withdrawal during owned carry and release; no obsolete continued delivery. |
| `clearance/chunk-dump` | Keep useful demanded chunk transfer with unrelated chunks unchanged; no global haulability assumption. |
| `control/pawn-activities` | Keep exact job execution, supported needs mechanics, preemption and refusal checks. It is an execution contract, not normal-planner economic evidence. |
| `control/pawn-activity-lifecycle` | Keep save/load, ownership, revision, continuation and release mechanics. |
| `control/pawn-activity-cooking` | Keep exact recipe/input execution and unavailable/reserved-target refusal. |
| `control/pawn-activity-delivery` | Keep exact quantities, blocked destination/filter legality and observed delivery. |
| `observations/pawn-activity`, `observations/needs-forecast`, `observations/coupled-forecast`, `observations/planned-site`, `observations/conditional-mood` | Keep native schema, coherent observations and explicit unknowns; none proves dispatch. |
| `needs/freeze` | Keep fixture control mechanics; production outcome cases must explicitly leave the needs they exercise live. |
| `medical/tend-gates` | Keep native doctor/patient eligibility and care protection. |
| `pawn/drug-policy` | Rewrite as explicit native policy creation/assignment and adult/child permission readback, followed by scheduled-prevention unsupported-state evidence. No automatic policy generator. |
| `pawn/drug-addiction` | Rewrite as explicit luciferium policy operation/readback and unsupported-state evidence on the prepared addicted pawn; no claim of successful autonomous treatment. |
| `mood/relief` | Delete the obsolete `relieve_need`/JobGiver choice contract. Preserve recreation completion and player/mental-state protection in the exact activity and normal-needs cases. |

Pure portfolio tests own distance/skill/capacity/obligation reversals, no-change,
unknown estimates, truncation and material exclusivity. Continuation tests use
explicit selected intents to isolate partial delivery, changed stacks, refused
dispatch, interrupted consumption, journal recovery and cleanup ownership.
They do not retain the retired food-family labor-per-unit winner as an oracle.

Other existing case assertions that rely on vanilla work after a designation
or bill must be audited before being counted as controlled execution evidence.
Keep their native legality/read/save assertions; replace unsupported execution
claims with a supported shared Method or an explicit unsupported result. Do not
delete physiological, involuntary, emergency or manual-player coverage merely
because it does not belong to ordinary economic selection.

A native-only case that probes a vanilla mechanic through a colonist's own job
choice (`burial/stranger_sarcophagus`, `burial/grave_over_morgue`) declares
`AcceptanceCase.VanillaColonists` with a Reason. The runner then calls the
test-only `test/vanilla_colonists` fixture op, so colonists choose voluntary jobs
natively for that game while the scenario clock holds the authority. A probe that
only needs game ticks to pass on an idle colonist (`apparel/dead_mans`) declares it
too, since the hold stops the clock on an unassigned voluntary pawn. It is
absent from production builds and is not evidence of controlled execution.

Native disorientation and the native imposed-sleep/force-job ability effects retain
their direct job authority through exact source-method scopes. The scope applies only
to the affected pawn, restores on exceptions, and records interruption/release of any
voluntary assignment. It does not authorize arbitrary LayDown or Goto jobs.

Actual colonist float-menu selection and immediate self-cast targeting revoke the
existing native authority before invoking their commands. This also preserves native
UI commands that start jobs without a playerForced flag; opening a target selector
alone does not grant a parallel execution permission.

## Audited routine labor admission boundary

A controlled Standard Method cannot directly admit repair, clean, haul,
recovery_service, ritual, ignite, equip, gear_replace, gathering, use_item, drop_equipment, movement, capture, rescue, tend, give_item or comms_trade_request. The journal rejects these with
unsupported_voluntary_method:<action-kind> before creating the plan or binding
its Method. This protects existing coupled pawn/resource claims; naming a
routine action "recovery" does not make it an emergency.

The audited callers return an AwaitingPlan refusal before claiming workers or
committing orders. Their Concerns remain unmet:

| Observed demand | Stable refusal subject |
| --- | --- |
| Ordinary gathering participation | unsupported_forecast_gathering |
| Neuroformer use | unsupported_forecast_neuroformer |
| Population acquisition with a lance | unsupported_forecast_population_lance |
| Precautionary disarm | unsupported_forecast_precautionary_disarm |
| Door passage | unsupported_forecast_door_passage |
| Population capture | unsupported_forecast_population_capture |
| Quest gift delivery | unsupported_forecast_quest_gift |
| Comms trade request | unsupported_forecast_trade_request |
| Door-to-wall replacement | unsupported_forecast_wall_replacement |
| Map/orbital trade opening | unsupported_shared_trade_labor |
| Preventative mood cast | unsupported_forecast_mood_cast |
| Caravan formation | unsupported_forecast_caravan_formation |
| Shuttle explicit loading / autoload enabled | unsupported_forecast_shuttle_loading |
| Ordinary weapon equipping | unsupported_forecast_equipment |
| Ordinary apparel replacement | unsupported_forecast_apparel_replacement |
| Essential structure repairs | unsupported_forecast_repair |
| Corpse storage delivery | unsupported_forecast_corpse_delivery |
| Quest monument delivery | unsupported_forecast_monument_delivery |
| Empty throne-room light | unsupported_forecast_throne_refuel |
| Routine turret refuel | unsupported_forecast_turret_refuel |
| Ideology ritual / bestowing | unsupported_forecast_ritual / unsupported_forecast_bestowing |
| Monolith interaction | unsupported_forecast_monolith |
| Quest grav inspection / site help | unsupported_forecast_grav_inspection / unsupported_forecast_site_help |

Supporting these outcomes requires exact candidate alternatives, shared
occupancy/input admission and observed completion in the existing forecast and
Method pipeline. The refusal is not evidence that the work is unimportant.
Configuration, designations, observations and explicit native mechanics remain.
Separately owned Recovery Incidents retain their emergency admission. Population
lance use serves acquisition demand rather than immediate protection and therefore
has the same unsupported ordinary-work boundary as neuroformer use.

Caravan actions distinguish home formation, routing an existing world caravan and
lawful site reform. Native checks the exact mode again at application: a route
cannot fall back to formation, and reform cannot start home-map packing. Existing
caravan returns and cleared-site extraction remain available. Shuttle autoload
creates loading lords and interrupts jobs, so enabling it has the same labor
boundary as explicit pawn loading; disabling autoload and launching a loaded
shuttle retain their native operation.

The audit includes Standard item use, delivery, custody and movement, and distinguishes actual Incident intent from its type name. The `economy/caravan` Need (#3316) cannot open a map or orbital session by assigning negotiator travel or comms work; existing-session operations and visiting-caravan settlement sessions remain available. A `people/mood/<pawn>` Need cannot dispatch preventative ability casts. Actual combat, mental-state containment, critical medicine and disaster recovery retain their existing protection owners. Dependent-patient critical rescue/tending uses the patient's `medical/critical/<pawn>` Need rather than duplicate Standard care. These boundaries preserve explicit native mechanics without claiming that unsupported ordinary outcomes have shared forecast alternatives. #3135 remains the completion owner.

Additional affected-case disposition:

| Case | Disposition |
| --- | --- |
| schedule/timetables, schedule/meditate | Deleted obsolete automatic timetable expectations; shared activity ownership replaces that writer. |
| pawn/self-tend | Explicit setting/readback, followed by ordinary review preserving the permission; no automatic treatment claim. |
| production/coupled-thermal | Prepared physical room; normal shared planner must close its roof, improve observed temperature and support occupied sleep. |
| wall/door-swap | Keep: explicit native deconstruction/build mechanics; normal door-to-wall labor reports unsupported until shared forecast support. |
| trade/routine, trade/browse | Prepared normal review asserts unsupported opening with no Trade admissions; explicit adjacent opening, priced staging and purchase/cancel preserve native goods/silver/session outcomes. |
| trade/settlement-roundtrip | Prepared home shortage asserts unsupported formation. A separately prepared existing caravan purchases settlement stock and returns with exact ExistingRoute; native home crew and purchased cargo prove delivery. |
| trade/open, trade/execute | Keep explicit native session mechanics; these do not prove autonomous negotiator selection. |
| combatlab/repair | Keep: explicit native combat mechanics, outside ordinary Standard selection. |
| recovery/service, recovery/area | Keep: separately owned emergency recovery and protection. |
| control/pawn-activities, control/pawn-activity-lifecycle | Keep: exact native mechanics, continuation stops, physiology and player takeover. |
| production/art | Rewrite: two prepared finished packed sculptures and a native exotic trader replace the long production precondition. Normal review reports unsupported trade labor with no Trade admission; explicit signed sale must move the exact packed item to trader inventory and increase colony silver. Native room/quality observations remain. Autonomous art manufacture and installation are unsupported and unproven; prepared art is not production evidence. |
| ideology/first-ritual | Rewrite: normal review must report unsupported ritual labor with no admitted Ritual action. A separate explicit intent carries native dialog assignments; after authority release the native lord completes and advances the ritual's finished tick. No automatic participant-selection claim. |
| mood/gathering | Rewrite: normal review must report unsupported gathering labor with no admitted Gathering action. Explicit native Party intent uses an observed legal organizer; assert exactly one native lord at the prepared spot. |

These registered cases require their stated native outcomes; source migration alone
does not establish a passing gameplay run. No outcome acceptance is inferred from
the admission fence alone.
