# Voluntary control

Vanilla owns baby feeding selection and mechanics. Food observes hungry dependents
and current provision without assigning or reserving a prospective caregiver. Active
feeding and carrying/return keep both caregiver and baby occupied independently
of a food-level increase. Native admission rechecks actual occupation. Other
native replacements are observed through the same takeover path.
See [support work](support-work.md#baby-feeding).

Bot work intent belongs to the shared Need/Method path:
native observations → candidate generation → ColonyForecast comparison →
Method admission and journal → Hands → observed completion and revision.
A proposal rejected by shared resource admission remains uncommitted and is reported
as deferred; existing Methods continue against current observations. An accepted
intent is not completed work. Native job selection remains available after the
requested first job. The labor migration
still has exact routine callers; their comparison and binding are pending migration.

An ordinary service-only eating/rest/recreation portfolio supplies no independent
reason for takeover, including with missing access or an ineligible pawn.
The comparator can explore it as a what-if complement to productive investments,
but a service-only winner is not dispatched; the binding entry point enforces
the same rule. Missing observation or no captured target establishes neither
failure nor future recovery. The general native baseline and intervention
assessment remain incomplete; see [current service evidence](forecast-contracts.md#current-native-need-response).

The [pawn activity contract](pawn-activities.md) owns exact execution and the
[continuation contract](method-continuation.md) owns release after the originating
Concern completes. [Portfolio selection](../architecture/portfolio-selection.md) owns comparison;
[support work](support-work.md) owns medical support and demand-driven deliveries.
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
Observed need targets have the same ownership during comparison and admission:
a bed or recreation target is owned whole for the Method, and an exact meal
stack is owned by the units its Method still owes, even when a proposed schedule
places their use at different times. Repeated use by the same participant remains possible.
A consuming Method owning future uses of a stack therefore leaves its unclaimed
remainder proposable and admissible for another Method (#3194): the observed
count less the units the journal's remaining Eat tasks owe, and the claimant
slots its owners leave, by the same arithmetic admission applies. Continuation
drops a task once its completion is observed; nothing is credited from predicted
consumption. Native binds a bite only to the observed lot holding at least the
owed count, so a stack that shrank, split, merged or moved below it releases.
The repeated-needs case supplies independent physical lots; `needs/shared-stack`
proves a second Method eating the remainder while the first still owns the stack.

## Reconsidering observed work

Current work is not automatically excluded from comparison. An exactly observed
stationary recreation-building service (the watch and sit-facing drivers, not billiards' shot loop) can be represented as retained work in the same
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
the released pawn's replacement admission waits for a fresh observation, and the
rest of the selection (other pawns' work that waits on none of the incumbent's
pawns or tasks) is admitted in the same review (#3598). A forecast of future work
after retention does not authorize a second owner for the same pawn.

## Control boundary and unsupported states

The native-led labor migration now permits native takeover after an installed
assignment offers its first job. Native overrides, constant selection,
opportunistic work and finalizers are not suppressed. Only an actual installed
replacement invalidates the old execution handle; worker continuation reconciles
the affected Method without restoring the displaced job over native care.
The [labor inventory](../../requirements/colony-labor.md) tracks routine exact
callers still awaiting migration; their existence does not make this work complete.

The native selection boundary distinguishes controlled voluntary work from
simulation and protection. Inactive authority and uncontrolled pawns retain
native selection. Downing, mental state, drafting, burning, forced player work
and queued orders are protection boundaries, not spare labor. Native exhaustion sleep and
the exact physiological Vomit job interrupt and release the voluntary assignment with
observed failure evidence. Native physiology,
legality, reservation checks and completion mechanics still run.

The shared selector may retain no change only for an observed, finite safe
interval. A missing estimate is not evidence that indefinite idleness is safe.
A controlled colonist with no committed coupled order runs vanilla job
selection under its work priorities (#3669; see
[pawn activities](pawn-activities.md#voluntary-ownership-and-bounded-no-change)):
every think-tree entry (selection, constant, override, opportunistic, finalizer
and job start) passes through for it, and native publishes nothing for it, so it
never holds the clock (#3518). A colonist that lost its activity Method, or whose
next service the comparison deferred, runs vanilla work until a review selects
something for it; it is no reason to review early, and there is no separate timed
defer and no explicit standby order. Bill work runs through native bills and
work settings (task 5.3 of `native-led-labor-execution`), so no exact order holds
bills a finished task could hand to another worker.
An unsupported need/state blocker is a visible finding
(`unsupported_voluntary_state:<pawn>:<blocker>`), not a capacity exclusion: the
pawn's supported needs and labor stay selectable, so it is fed and worked like any
colonist, while incumbent retention still excludes it (#3557). The blocker is not autonomous coverage of the unsupported need.

The native support check admits the exact need types Food, Rest, Joy, Mood,
Comfort, Beauty, Outdoors, Indoors and RoomSize. Derived/modded classes do not
inherit that claim. An inactive `Need_Chemical_Any` hidden by the native
`ShowOnNeedList` rule is exempt; a visible chemical need is not. Psylink, active
hemogen/dependency genes and non-removed chemical-dependency hediffs add their
specific blockers. A scheduled drug produces either `scheduled_drug_due` or
`scheduled_drug_onset_unassessed`; a missing drug definition produces
`unknown_scheduled_drug`. Supported ordinary needs do not cancel another blocker.

The bounded 600-tick `Idle` defer is gone (#3624), and so is the explicit
`Standby` order that followed it (#3669). Vanilla work claims no capacity and a
coupled order preempts it as soon as review selects anything, including eating or
sleeping for a falling need, so designations, bills and zones with no coupled
binding are still worked; the coupled selection is the override, not the only
source of work. A completed or failed
assignment keeps its exact ownership pending continuation or release, but stops the existing
Supervisor clock; readmitting time cannot turn that terminal assignment into indefinite idle.

| Domain | Owner and present boundary |
| --- | --- |
| Food, rest, recreation | Shared candidates and forecast; exact ingestion, bed and recreation-building execution through Hands. Recreation without a building needs observed candidates and exact adapters before it becomes a planner choice. |
| Harvest, food conversion, material delivery, construction, roof work, clothing | Shared task composition and selection, with observed input/output rebinding. A delivered garment is not evidence it was worn or that physiological cold exposure improved. |
| Medical support | Shared support candidates; exact pending service identities and native conditional room factors. Critical medical protection remains with its existing emergency owner. No fabricated prognosis. |
| Fire/combat/player protection | Existing protection and emergency paths retain authority; an ordinary Method cannot convert protected occupancy into free capacity. Vanilla fights home-area fires; the brain has no fire Method. Native releases the committed orders of capable colonists near a home-area fire, so they fight it through vanilla job selection ([home fire](pawn-activities.md#home-area-fire), #3672). |
| Drugs, addictions, genetic dependencies, hemogen, psyfocus, unsupported or modded needs | Native observations expose blockers; the pawn's supported needs and labor stay selectable. Ordinary safe-idle admission cannot cover them. A future Method must supply onset/effect observations and exact execution under the existing Concern owner before support can be claimed. |
| Mining, tree cutting, hunting, research, repair, refuel, cleaning, firefighting, non-supported production and colony services | Concerns stand the demand (designations, bills, zones, work priorities); a colonist with no committed order works it through vanilla job selection (#3662, #3669). That is native execution, not a planner-owned choice: a service the brain must decide against the forecast (who, when, at what cost) gets an exact shared Method that preempts it. |

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

- `ColonyReview.ReviewMeals` calls `ReviewMealTier` to derive cooking readiness;
  `Paste` and `PasteSite` also use meal-tier review for dispenser planning.
- `FoodPlan` calls `SupplyFoodPlan`; `ResourceSupply` calls
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
current passing gameplay run. Registered cases and successful compilation are
not gameplay evidence: that comes from the nightly tier (`coordination/*`,
`food/*`, `shelter/*` cases), the on-demand cases by name and the
[colony review](../testing/colony-review.md) season, as reported on the owning
epic (#3124).

| Exact case | Disposition and retained claim |
| --- | --- |
| `production/coupled-needs` | Rewrite/extend for normal shared selection through real ingestion, rest/wake and recreation, revision and release; preserve a legacy Work timetable as an adversarial fixture, not an expected policy. |
| `coordination/food-delegated` | Keep real harvest/output/delivery and participant ownership; compare alternatives in pure tests rather than asserting a mandatory helper tactic for every world. |
| `coordination/food-carried-recovery` | Keep interrupted carry, exact remaining quantity and observed recovery/release. |
| `coordination/construction-delegated` | Keep the urgent sleeping shelter bound before vanilla builders take it (#3537), actual material transfer, frame rebinding to the dropped stack and building completion. The selected shell must reduce positive forecast thermal capability loss, with native comfort minima checked against assessed exposure inputs; actual occupied warming remains separate evidence. |
| `production/clothing-activity` | Keep normal selected-stuff production and input conservation; do not equate production with wearing. |
| `production/clothing-activity-unfinished` | Keep observed unfinished-item resumption and conservation, without spending loose inputs twice. |
| `control/pawn-activities` | Keep exact job execution, supported needs mechanics, preemption and refusal checks. It is an execution contract, not normal-planner economic evidence. |
| `control/activity-*` | Keep save/load, ownership, revision, continuation and release mechanics. |
| `control/pawn-activity-delivery` | Keep exact quantities, blocked destination/filter legality and observed delivery. |
| `observations/pawn-activity`, `observations/planned-room-prediction` | Keep native schema and coherent observations; a fact that cannot be read fails the case ([rule 9](../architecture/rules.md#review-items)); none proves dispatch. |
| `needs/freeze` | Keep fixture control mechanics; production outcome cases must explicitly leave the needs they exercise live. |
| `medical/tend-gates` | Keep native doctor/patient eligibility and care protection. |
| `pawn/drug-policy` | Rewrite as explicit native policy creation/assignment and readback on a luciferium-addicted colonist, followed by scheduled-drug unsupported-state evidence (absorbs `pawn/drug-addiction`). The policy contents come from the drug-policy planner's Medical entries (#3498). |
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

A native-only case runs no brain, so its controlled colonists own no order and
choose voluntary jobs natively (#3669): a case that probes a vanilla mechanic
through a colonist's own job choice (`burial/stranger-sarcophagus`,
`burial/grave-over-morgue`, `control/unassigned-runs-vanilla`, `control/home-fire-release`) needs no opt-out.
The fixtures whose staged targets an idle colonist must leave alone (pawn cooking,
pawn delivery, sow) turn the unowned colonists' work priorities off
(`IdleColonists` in `scripts/fixtures/FreezeNeedsFixture.cs`).

Native disorientation and the native imposed-sleep/force-job ability effects retain
their direct job authority through exact source-method scopes. The scope applies only
to the affected pawn, restores on exceptions, and records interruption/release of any
voluntary assignment. It does not authorize arbitrary LayDown or Goto jobs.

Actual colonist float-menu selection and immediate self-cast targeting revoke the
existing native authority before invoking their commands. This also preserves native
UI commands that start jobs without a playerForced flag; opening a target selector
alone does not grant a parallel execution permission.

## Audited routine labor admission boundary

A controlled Standard Method cannot directly admit repair, haul,
recovery_service, ritual, equip, gear_replace, gathering, use_item, drop_equipment, movement, capture, rescue, tend, give_item or comms_trade_request. The journal rejects these with
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

The audit includes Standard item use, delivery, custody and movement, and distinguishes actual Incident intent from its type name. The `economy/caravan` Need (#3316) cannot open a map or orbital session by assigning negotiator travel or comms work; existing-session operations and visiting-caravan settlement sessions remain available. A `people/mood/<pawn>` Need cannot dispatch preventative ability casts. Actual combat, mental-state containment, critical medicine and disaster recovery retain their existing protection owners. Dependent-patient critical rescue/tending uses the patient's `medical/critical/<pawn>` Need rather than duplicate Standard care. These boundaries preserve explicit native mechanics without claiming that unsupported ordinary outcomes have shared forecast alternatives. Supporting one of them is its own issue under the Need that owns the demand.

Additional affected-case disposition:

| Case | Disposition |
| --- | --- |
| schedule/timetables, schedule/meditate | The old template cases remain deleted. Current mechanics-based regular preferences are covered by `NativeTimetableTests` and settings journal/Hands regressions; they do not prove service execution. See [work schedules](work-assignment.md#schedules). |
| pawn/self-tend | Explicit setting/readback, followed by ordinary review preserving the permission; no automatic treatment claim. |
| production/coupled-thermal | Prepared physical room; normal shared planner must close its roof, improve observed temperature and support occupied sleep. |
| wall/door-swap | Keep: explicit native deconstruction/build mechanics; normal door-to-wall labor reports unsupported until shared forecast support. |
| trade/routine, trade/browse | Deleted (#3404): the unsupported-opening verdict is a planner choice (admission covered by IncidentVoluntaryAdmissionTests); paid purchase and cancel are trade/execute and trade/settlement-session. |
| trade/settlement-roundtrip | Deleted (#3404): the unsupported-formation verdict is a planner choice, the purchase repeats trade/settlement-session, and the ExistingRoute return is world travel past the staged tick cap. |
| trade/open, trade/execute | Keep explicit native session mechanics; these do not prove autonomous negotiator selection. |
| combatlab/repair | Keep: explicit native combat mechanics, outside ordinary Standard selection. |
| recovery/service, recovery/area | Keep: separately owned emergency recovery and protection. |
| control/pawn-activities, control/activity-* | Keep: exact native mechanics, continuation stops, physiology and player takeover. |
| ideology/first-ritual | Rewrite: normal review must report unsupported ritual labor with no admitted Ritual action. A separate explicit intent carries native dialog assignments; after authority release the native lord completes and advances the ritual's finished tick. No automatic participant-selection claim. |
| mood/gathering | Rewrite: normal review must report unsupported gathering labor with no admitted Gathering action. Explicit native Party intent uses an observed legal organizer; assert exactly one native lord at the prepared spot. |

These registered cases require their stated native outcomes; source migration alone
does not establish a passing gameplay run. No outcome acceptance is inferred from
the admission fence alone.
