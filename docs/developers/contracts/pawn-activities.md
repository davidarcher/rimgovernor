# Planner-owned pawn activities

`PawnActivities` is the existing Hands intent for atomic exact assignments. Its
Method journals selected tasks, prerequisite edges, participants, absolute departure
windows and remaining obligations. It has no separate scheduler or workflow store.
Food acquisition and material delivery/building use the same ColonyForecast search,
binding, journal continuation and native ownership contract.

[Evaluated support work](support-work.md) uses these same contracts for cleaning,
care preparation and demand-driven delivery. It replaces native automatic
bench-cleaning, refill withholding and global chunk hauling. Supported voluntary needs
share this same owner; legacy timetable and mood need-giver writers are removed.

## Live harvest and delivery

Normal review supplies the existing shared food demand to the coupled portfolio.
Native reads enumerate capable harvesters and haulers separately, route estimates,
carrying limits and allowed storage cells. Policy proposes self-service and delegated
harvest/delivery combinations, including useful approach before harvest finishes.
Adding a low-capacity helper creates a smaller alternative; it never shrinks every
self-service option. Shared availability, work, travel, waiting, displacement,
deadline and explicit recovery estimates feed the bounded common comparator.

Native route costs are estimates, not calibrated toil durations. Current live
callers assume equal unit labor rates, zero recovery cost on Danger.None routes,
and predictable prerequisite timing with divisible alternative work. Unknown
routes/windows are not zero. Retaining/postponing wins when no affordable complete
alternative is known. These are bounded forecast assumptions, not evidence of
expert play. The [portfolio contract](../architecture/portfolio-selection.md)
defines coupled consequences, comparison and current search bounds.

Raw delivery and eligible cooking remain alternatives until shared comparison.
After observed harvest completion the selected Method rebinds actual output;
continuation does not locally choose another recipe. Selected recipe, bench,
bill, ingredient definition and counts remain policy decisions.

## Live one-iteration cooking

Observed eligible existing bills offer bounded repeated supply/cook/output-delivery
alternatives through the same comparator and selected-task lifecycle. Native
reports exact slot counts, pawn-adjusted work, nominal nutrition and routes; it
neither chooses recipes nor creates bills. The current aggregate cooking route
supports observed participant and route alternatives. Shared composition can
combine partial native-certified stacks of the same ingredient definition.
Heterogeneous ingredient substitution and hypothetical benches without observed
bill intent remain unsupported.

Supply completion permits fresh exact ingredient rebinding at the selected bench
staging area. Recipe completion permits only observed matching output delivery.
Failed consuming work releases; nearby food cannot authorize replay, refund or
inferred consumption. Nominal nutrition is not eater-specific survival value.

## Live material delivery and building

The ordinary building planner considers sites from its existing Method history.
Native reads observe each site's material demand, eligible suppliers/builders,
source/return/site routes, carrying capacity and construction work. Policy builds
a supply prerequisite and finishing task, compares self-service with delegation,
and uses the same selected-task binding/continuation as food. Supply preparation
approaches the source; finishing preparation approaches the site. Native rebinds
blueprints to frames and reads remaining demand before each bounded trip. Actual
finished buildings, not accepted orders or successful supply, satisfy finishing.

The live census supports one required material with a sufficient observed source
stack, repeated capacity-bounded trips, or an already-supplied site. Mixed-material
sites and missing whole source stacks produce explicit findings. Multi-source
assembly and arbitrary construction recipes remain unimplemented. Reverse route
costs are observed separately; pickup/setup time and route changes remain estimate
limits. Native construction tiers, skill/capacity guards and reservations still
veto exact work. Existing construction demand owns the material budget.

## Live sowing

Sowing is a cell-targeted Work step through the grower sow giver (`WorkGiver_GrowerSow`), not a
new activity kind. Native reads, for every free colonist and every growing zone or planter with a
crop set, the cells the giver would accept now (crop set, cell free, fertility and season, adjacent
sow block, the pawn's Plants level against `sowMinSkill`, reservable and reachable), the pawn's
`PlantWorkSpeed`, the crop's sow work and the route to the nearest cell. Legality is the game's
own giver read through a private subclass that sets the giver's shared wanted-plant cache around
each call and accepts only a Sow job, so a cell the giver would answer with a cut-plant or
haul-aside job is not sowable and the tree-lattice patch applies. Policy proposes one task per
(pawn, zone) with as many cells as fit the horizon at that pawn's speed (`sowing_cells_truncated`
when it cuts); a zone is one exclusive outcome, so one pawn sows it at a time. The zone's crop is
read, never chosen, and a zone with no crop is reported (`sowing_zone_without_crop`). Nothing
values the sowing yet: the roots carry no consequence until the project graph credits them.

The step is `Destination` = the first cell, `Target` = the zone's (or planter's) unique load id and
`Count` = the cells to sow, validated like a BuildRoof step (a cell is required, and a positive
count). `JobDriver_PlantSow` sows one cell and ignores queued targets, so native offers one Sow job
per cell and keeps a cells-sown counter on the in-memory execution entry: the step stays current,
re-offering the nearest legal cell, until the count is sown. A step with no legal cell left ends
without a fault (a full zone is done); a load restarts the count, as it restarts the entry.
Continuation rebinds the nearest legal cell for the pawn and treats a fully sown grower as done.

## Continuation, observations and bounds

A completed participant can progress while another keeps its byte-equivalent
assignment and revision. Unknown evidence preserves useful work. Known Concern
recovery, timeout, takeover or consuming failure triggers cleanup of still-owned
participants; a newer owner is never released. A refused batch retains predecessor
cleanup responsibility. Fresh exact terminal/held readback projects availability
through the existing WorkResources owner, while claims against other Methods stay
in force. It does not invent fresh inventory from ownership evidence.

Native output binding reads current identities and quantities: local split/merged
stacks, previously bound stacks moved elsewhere, or the selected pawn's carried
stack. Fungible local stock is not individual-output provenance. Arbitrarily moved
replacement identities outside the source neighborhood are not inferred. Delivery
failure can resume exact carried input with at most three retries under the original
deadline; consuming work cannot replay. Blocked selected destinations release for
normal reconsideration. No hidden storage fallback changes the chosen destination.

Native reads and family providers bound participants, stacks, sites and route
probes. The [portfolio search limits](../architecture/portfolio-selection.md#dependencies-and-search)
apply to shared composition and comparison. Truncation and unknown estimates
remain explicit; omission never proves inferiority.
No prospective output is credited as current inventory. Shared composition and
portfolio comparison account for supported material, equipment and pawn claims
and coupled needs consequences. Unsupported mechanics remain explicit; the
bounded result is not a globally optimal colony plan.

The registered `coordination/food-delegated`, `coordination/food-carried-recovery`
and `coordination/construction-delegated` prepared cases use normal Rounds and
Hands. Their fixture establishes facts, captures exact installed ownership and
injects one carrying failure; it installs no chosen tasks. They assert native
completion, physical output and observed cleanup. Registration/compilation does
not establish that these game scenarios have passed; nightly execution supplies
that evidence. Pure and journal tests cover choice reversals, partial progress,
rebinding, unknowns, deadlines, refusal and stale atomic revisions.
## Exact activity execution

The existing executor journals the domain action and dispatches its typed intent
through Actions/Apply. Installation acknowledges authorization, not started or
completed work. Each order carries assignment/Method identity, an expected and
new revision, exact pawn, and a bounded list of authorized steps. The surrounding
Apply identity and existing authority generation scope the batch. Native validates
all orders before replacing any entry. A repeated identical installed revision
is a no-op; a conflicting/stale revision is refused. Release uses the same path.

Normal Method admission checks that every activity order, including release,
names the Method being committed. Across that Method's plan, a pawn may appear
in only one activity action: a sequence of revisions requires observation and a
later decision, not separate competing actions in the same plan. Multiple pawns
may share one atomic batch. These checks run in the existing owner transaction,
before plan creation or Method binding; refusal leaves both unchanged. They do
not reserve pawns across different owners or prove current native availability.

The initial step families are exact thing-target work (Clean, Repair, Harvest, Research and BeatFire through a named native WorkGiver; Research targets a research bench and works the current project, which the research Concern owns), selected food/count,
selected bed/rest, selected horseshoes recreation, exact delivery, exact cooking, material Supply, FinishFrame, Approach, Hold, bounded Idle and Standby (explicit idle). Unsupported families are refused, not sent to a
native economic selector. Other recreation adapters remain outside this
initial execution slice. Firefighting is an explicit policy-selected emergency revision, not an automatic native fallback. Work givers use forced:false, required capacities,
capabilities, native target checks and native reservations. They may still perform
the normal internal mechanics of their chosen job; this does not model every toil
as a separate economic decision.

`Deliver` names a currently spawned item, a positive count and an exact destination
cell. The custom delivery driver uses native pathing, reservations, stack splitting
and carrying, but does not ask the hauling WorkGiver to select storage. It never
collects nearby duplicates or substitutes a nearby drop location. Admission and
execution require a capable hauler, sufficient source quantity and carrying capacity,
an empty carry tracker or the exact selected carried stack, and a reachable, unreserved destination without loose items.
This initial adapter deliberately refuses stack merging and multi-trip deliveries.
The planner must split larger transfers into bounded attempts and observe each result.

Destination availability is rechecked during execution. A changed destination,
lost source, capacity shortfall or deadline fails the assignment visibly. Completion
requires the selected count dropped directly at the selected cell. An interruption
after pickup may leave food carried by the pawn: failure is not evidence that it is
still at the source, and a new planner decision must inspect that state. An exact carried stack can resume after fresh observed binding. Newly harvested/cooked items
require fresh observed identities and a revised assignment; future output IDs are
not guessed.

`Recipe` names the exact bench, existing bill, recipe, cooking WorkGiver and up to
16 distinct ingredient stacks with positive counts. It authorizes one recipe
iteration, regardless of the bill's remaining repeat count. Bill creation and
configuration remain separate Hands actions; cooking neither creates nor edits a
bill. The selected bill must explicitly use DropOnFloor. Native production places
the output near the worker; observing its actual identity/location and assigning
delivery is a separate planner decision, not native storage selection.

The initial cooking adapter supports ordinary single-product nutrition recipes
with the base RecipeWorker, no unfinished item or special products, and one
ingredient definition across all selected stacks. Every recipe slot must accept
that definition; selected quantities must exactly equal the sum of native slot
requirements, including nutrition-based requirements. Mixed ingredients are
refused rather than resolved locally. Eligibility includes the selected bill's
filters, worker restrictions, native skills/capacities, bench recipe availability,
fuel/power usability, ingredient radius, reachability and reservations.

The custom cooking driver uses native ingredient collection, recipe work,
consumption and product generation with the already-selected ingredient queue.
It does not call the WorkGiver's bill/ingredient selector. All selected inputs and
the bench must reserve successfully; loss or shortage of placed ingredients,
an unusable/forbidden bench, bill deletion/suspension, or a changed recipe/store
mode fails visibly. It verifies complete input quantities before work and again
before consumption. Output quantity, poisoning and other native effects remain
RimWorld-owned. A successful job still requires physical output observation for
forecast credit. Interrupted collection can leave carried or placed ingredients;
there is no automatic retry, refund or assertion that those inputs remain at
their original locations.

For owned pawns the normal and constant think-tree selection paths are intercepted.
Override probes cannot consume/restart assignments. Opportunistic prefixes and
optional finalizer jobs are suppressed; the underlying driver's cleanup and
reservation handling still run. Existing queued/player-forced work refuses initial
acquisition instead of being erased. Native work priorities and timetables do not
select activities inside this boundary. Uncontrolled pawns keep native behavior.

A selected native Job is identified by both reference and load ID, so pooling
cannot transfer ownership. Actual StartJob and EndCurrentJob hooks distinguish an
offer from execution. Native reservation failures terminate the attempt visibly.
Successful work advances only to an explicitly authorized next step; failed or
interrupted work never silently restarts. A new revision is required to retry.
The longer-lived Method can survive interruption and submit a new attempt through
the [journal continuation path](method-continuation.md#pawn-activity-methods).
Worker cleanup remains active after Concern recovery and releases failed or
expired installed assignments. Productive continuation requires terminal native
readback and current exact input binding by its owning planner.

GovernorWait has no opportunistic prefix or player-forced flag. Absolute game-tick
deadlines never reset when revisited. The first dependency adapter holds on an
exact known filth reference: its destruction makes the authorized continuation
eligible; a missing/unspawned dependency is not guessed complete. More complex
dependencies require policy revisions. A deadline without release fails visibly.
Sleep and recreation are bounded by their deadlines and may finish earlier through native recovery.
An exact admitted recreation job ignores the legacy timetable's joy permission:
the Method owns its service window. Native joy gain, tolerance, target legality,
player authority and the absolute deadline still apply.
Native exhaustion sleep and the exact Vomit job may interrupt an assignment; the
interruption records failure and releases ownership before native physiology runs.
Completed or failed assignments awaiting continuation stop the existing Supervisor;
retained ownership does not authorize indefinite idle time.
Work deadline exhaustion is a failure. Native food/rest levels and physiological
consequences are not modified by execution.

Completion/failure holds the clock through the existing requested
pause journal path, exposing typed readback for replanning. A terminal coordinated participant waits without repeatedly pausing useful work by other participants. A nongraph assignment still pauses while awaiting a decision. Downing, mental state or drafting encountered at selection/start
pauses and releases this voluntary-work owner; native involuntary behavior and
the existing combat owner are not replaced. This is not a complete autonomous
emergency-response policy; full-control production cutover requires that coverage.

Authority generation changes, game changes and despawn invalidate the transient
table and end matching owned jobs without clearing unrelated jobs. Assignments
are not saved. A restored native job has no restored planner authorization;
normal authority and current facts must authorize new control after load.

## Observation and tests

Live food policy separates candidate generation, generic comparison and exact
binding under the [enforced stage contract](../architecture/colony-forecast.md#live-activity-policy-stages).
The binder consumes the shared forecast's selected outcome and concrete binding map. Lifecycle ownership checks,
the Method journal and Hands retain their existing responsibilities.

Pawn observations expose `ActivityExecution` (assignment, Method, revision, step,
state, reason, current native job ID and tick). `pawn_activity_execution` records
execution changes in the existing OTLP ring. `pawn_activity_interval` includes
known assignment/Method attribution; neither job success nor installation is
physical output evidence. No additional durable history is created.

`control/pawn-activities` uses a staged hungry/tired producer and helper to
verify cleaning dependency, harvest, selected meal and selected bed. Each
`control/activity-*` case makes one lifecycle claim over the same installed
executions: batch atomicity, hold deadline, native work refusal, emergency
preemption of selected recreation, release, native overrides, idle completion,
player takeover and real save/load invalidation (`control/activity-reload`).
`control/work-resources` reads the installed ownership back through colony facts. Pure tests round-trip the
Hands action through the session codec and intent builder and reject malformed
batches. `clean/bench`, `clean/care` and `clean/care-routine` prove the evaluated
[support Methods](support-work.md) that replaced the automatic cleaning and
hauling helpers (#3149). The activity observation case remains
independent native lifecycle/output coverage.

`PawnActivityMethodTests` commits coordinated assignments through a real SQLite
Method journal and builds the Hands intent from the reloaded action. It checks
pending progress, the existing open-Method guard, wrong-Method refusal (including
release), repeated-pawn refusal across actions and transaction rollback. This is
admission/payload evidence, not a live forecast caller or native dispatch test.

`control/pawn-activity-delivery` starts from a prepared ripe crop and checks edible
harvest output, refusal when the selected delivery cell becomes occupied after
installation, and an explicit new revision delivering one unit to another exact
cell while conserving the source quantity. The fixture drives those revisions
through native activity installation; it does not prove forecast selection, normal
Rounds/Hands dispatch or autonomous recovery. Domain tests separately round-trip
the exact delivery payload through the journal codec and Hands intent builder.

`control/pawn-activity-cooking` prepares a fueled stove with a competing bill and
unselected food. It checks wrong-quantity refusal without changing ownership,
idempotent installation, failure after losing a selected stack or blocking the
bench, then one explicit revision's exact ingredient consumption, edible output
and selected-bill decrement. The other bill and unselected stack must remain
unchanged. This fixture-driven execution case does not establish live forecast
selection or autonomous recovery. Journal/Hands intent tests preserve the cooking
recipe, bill and ingredient identities/counts and reject ambiguous payloads.

## Needs forecast support matrix

The [needs forecast](../architecture/colony-forecast.md#needs-alternatives-and-future-capacity)
is generated by ordinary review and selected by the shared
[portfolio comparison](../architecture/portfolio-selection.md); the
[voluntary-control](voluntary-control.md) cutover made it the only chooser of a
controlled pawn's eating, sleeping and recreation. The existing
Method journal owns admitted work and Hands writes exact assignments; a candidate
does not reserve a pawn or authorize native fallback.

| State or choice | Forecast and ownership behavior |
| --- | --- |
| Ordinary loose permitted food | Enumerate observed edible stack/count alternatives; consumption only after completed ingestion; nutrition waste explicit. Native mechanics choose the chewing position, whose extra travel/mood is unassessed. |
| Eligible existing bed | Enumerate timing and partial/full recovery; travel occupies the same pawn. No ground-sleep adapter is inferred. |
| Horseshoes | Enumerate exact observed pin with current tolerance-adjusted gain. Future tolerance and playing-position travel remain unassessed. |
| No eligible target / exhausted bounds | No feasible candidate or explicit truncation, never an instant need or permission for native AI to choose. |
| Interrupted needs | Partial rest/recreation service only; unfinished eating contributes no forecast food. Reobserve before journal continuation/release; no replay credit. |
| Drafted / player-controlled / queued or player-forced work | No voluntary candidate. Existing player/combat authority owns the pawn; a timetable bypass cannot override it. |
| Downed / dead / mental state | No voluntary candidate or supported productive capacity. Native involuntary behavior and existing protection remain authoritative. |
| Unknown mechanics / occupied job duration | Unknown capacity, never free time. |
| Other recreation, DLC/mod needs, babies or special ingestion | Explicit unsupported effects/targets; no inferred adapter or hidden fallback. The pawn is excluded from ordinary capacity and the state is visible through the Supervisor hold ([control boundary](voluntary-control.md#control-boundary-and-unsupported-states)). |

Needs choices use the same Method journal and Hands ownership as productive work. Ingestion timing belongs to selected activities; there is no separate food-bar override or native food-seeking threshold writer.

## Exact tailoring recipes

The generic `Recipe` activity carries a `PawnActivityRecipe`: exact recipe, bill
(if already present), stuff, input identities/counts, or exact unfinished item.
The ordinary textile/leather path is described in
[equipment upkeep](equipment-upkeep.md#live-clothing-requirement-selection).
Hands validates the entire batch before installing it. A new recipe's private
`Bill_GovernorRecipe` is provisioned only at verified job start; it cannot be
selected by native priority scanning (`ShouldDoNow` is false). It remains unsuspended
because native recipe work treats suspension as an interruption. Existing player
bills are never deleted.
Provisioning and cleanup use the existing admitted-write attribution scope so these
operations do not masquerade as player bill edits. Owned bills without unfinished
work are cleaned up on job termination; actual
unfinished work retains its native bill, creator, stuff, ingredients and work.
The native bill class identifies this activity execution anchor to the standing
production ledger. It neither satisfies a standing bill declaration nor becomes
an orphan for ledger removal; exact explicit player/native removal remains legal.

Selected task output matching requires both product definition and selected
stuff. A completed job is not product evidence: delivery binds an actually
observed stack and an exact empty destination. Failure releases the assignment;
a later normal equipment review may select the observed unfinished item as a
new Method. It never replays a consuming operation based on a missing receipt.
Observed native UFT conversion removes the loose-input claim while preserving
pawn/bench ownership. The session journal records only the selected attempt;
all rejected alternatives and forecast graphs are transient.

## Voluntary ownership and bounded no-change

Active authority intercepts voluntary selection for eligible controlled colonists,
including constant think-tree checks, opportunistic work and finalizers. Existing
exact orders may finish; player work, drafted pawns, involuntary states and
burning retain their native or protective owner. Changing authority interrupts
unowned voluntary work so it cannot silently continue under the new policy.

When comparison retains no change for an available pawn, policy can bind Idle
through the same Method. Its observed interval ends within 600 ticks or at the
first supported need threshold, whichever comes first. Unknown needs, unmet
needs, explicit unsupported-state blockers and shared claims cannot authorize
that interval. Idle completion wakes review and release; it creates no inventory
or recovery benefit. Hold remains solely an already-authorized task dependency.

Under brain authority every controlled colonist holds a Hands assignment (#3322).
After a round's coupled selection, the brain gives each available, unoccupied
colonist that no admitted plan, in-flight activity action or native assignment
takes an explicit `Standby`: one order whose single step has no target, count,
workgiver, task or deadline (`standby-<id>` Method, revision zero). Native
execution offers a colony wander (`JobGiver_WanderColony`, else `Wait_Wander`),
never work. Standby claims no pawn or target, so selected work, eat or sleep
acquires the pawn from revision zero and the standby Method ends; work candidacy
and food/support availability treat a standby pawn as free. Standby never
steps, completes or fails: a wander ending re-offers it, and drafting, an
involuntary state, a player order or an admitted direct job (hunt, equip) yields
it quietly (`released`) instead of faulting. Needs are met by the brain's ordinary
selection replacing standby, not by standby itself.

An eligible pawn without an authorized activity returns no native voluntary job
and stops the existing clock as unavailable, naming that pawn. It does not
silently start native work or represent an unsupported condition as a successful
indefinite wait. See [supported scope and remaining owners](voluntary-control.md).
Native disorientation and the native imposed-sleep/force-job ability effects retain
their direct job authority through exact source-method scopes. The scope applies only
to the affected pawn, restores on exceptions, and records interruption/release of any
voluntary assignment. It does not authorize arbitrary LayDown or Goto jobs.

Early native completion before the next selected task's departure installs that
task's bounded preparation under a new journal revision. The terminal old order
does not grant clock progress; preparation preserves the original deadline and
waits for the selected departure and observed prerequisites.

Selected range/sparring use the existing Work step with a typed practice mandate and reciprocal exact participants. See [assigned capability maintenance](capability-maintenance.md) for floor ownership, bounded execution and uncertainty. Native autonomous roster/skill selection is disabled under active bot authority for the current map; uncontrolled behavior remains native.
