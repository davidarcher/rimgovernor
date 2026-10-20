# Planner-owned pawn activities

`PawnActivities` is the existing Hands intent for atomic exact assignments. Its
Method journals selected tasks, prerequisite edges, participants, absolute departure
windows and remaining obligations. It has no separate scheduler or workflow store.
Food acquisition and material delivery/building use the same ColonyForecast search,
binding, journal continuation and native ownership contract.

[Evaluated support work](support-work.md) uses these same contracts for
care, surgery and demand-driven delivery. It replaces native automatic
refill withholding and global chunk hauling. Supported voluntary needs
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

## The set step

A set step (#3613) is a Work step that repeats over a set of things or cells, the Sow step's
pattern. `PawnActivityStep.Targets` names the members as thing ids and `Cells`
(`EquatableArray<Cell>`) is the cell form (#3615; RemoveFloor, and RemoveRoof over the roofed cells
a roof-holder leaves unsupported, #3617). Empty keeps the single-`Target` behaviour. A set
step is Work only, its members are distinct, it names things or cells, not both, and `Count` is the
set's size; `SameOrder` compares the sets with the rest of the step.

Native (`NativeSetActivities`) offers one job per member, nearest first: a member that is gone,
already done or no longer legal is skipped, and the step completes when `Count` members are done or
none is left legal (reason `no_legal_member`: a cleared set is done, not a fault). Validate refuses
only when a member is left to do and none is legal now, and names every member's reason. Cut is the
thing family:
Work/CutPlant through `WorkGiver_PlantsCut`, where each member's job is the giver's own for a
designated plant (`CutPlantDesignated`, or `HarvestDesignated` where a chop-wood HarvestPlant stands)
and legality is the giver's checks less the designation (a plant, not burning or forbidden, the pawn
willing to cut it, cutting not prevented, reservable and reachable). `Apply` stands the CutPlant
designation (the ordered cut designator's, so a tree too) on every member lacking a cut or harvest
designation and marks its enclosure-guard record `Ordered`, as a Deconstruct or Uninstall step does
on its building; no job hook rechecks a CutPlant record (the enclosure guard judges buildings), so
the record serves the release on revocation and the removal hook alone. RemoveFloor is the cell
family (#3615): Work/RemoveFloor through `WorkGiver_ConstructRemoveFloor`, one `JobOnCell` per member
cell nearest first, as the BuildRoof cell branch; a cell whose top layer is gone
(`CanRemoveTopLayerAt` false) is done and skipped, and legality is the giver's `HasJobOnCell` less
the designation (not fogged or forbidden, no building needing the floor, reservable on the floor
layer and reachable). `Apply` stands the RemoveFloor designation (the plain designation the
reconcile's FloorOut stands) on every member cell lacking one and marks an enclosure-guard record
keyed by the cell `Ordered`; the record serves the release on revocation alone (the guard's
designation lookup finds a thingless record at its cell). Mine is the third family
(#3616): Work/Mine through `WorkGiver_Miner`, where each member's job is the miner's own `Mine` job
(`MineAIUtility.JobOnThing`, which refuses an undesignated cell; its haul-aside of a blocking item is
not accepted as the member's work) and legality is the giver's checks less the designation plus the
mine_safety guard's permanent blockers: mineable rock, the excavation cell rule (`CellBlocker`) and
the excavation safety rule (`ExcavationSafety.Check` Supported for the one cell), not forbidden,
reservable and reachable. A rock whose removal would leave a roof unsupported is skipped, and
Validate names it when no member is legal. `Apply` stands the Mine cell designation (the mine
designator's) on every member whose cell has no Mine or MineVein and marks its mine_safety record
`Ordered`, standing or Designate-stood (such a record waits for supervised play until it is Ordered);
the pick hook rechecks the record before every hit as it does any mine_safety record. RemoveRoof is
the second cell family (#3617): Work/RemoveRoof through `WorkGiver_RemoveRoof` (def `RemoveRoofs`)
over `Cells`, one `RemoveRoof` job per roofed member nearest first (`JobDriver_RemoveRoof`, 65 work
at `ConstructionSpeed x 1.7` a tick, which also runs the game's own flying-roof check after each
cell); a member already unroofed is done, and legality is the giver's checks less the NoRoof area
(not fogged, no thick roof, the ceiling reservable, the cell reachable). `Apply` puts every roofed
member in the NoRoof area through `Designator_AreaNoRoof` (which also clears BuildRoof), the area
the giver and job require; it stands no designation and no guard record. The done count lives on
the in-memory execution entry with the sow count; a load restarts it.

Continuation rebinds a set step to the members the pawn can still work, nearest first (`Targets` or
`Cells` the legal members, `Count` and `RemainingCount` their number), and treats a set with no
member standing (no thing on the map, no cell still to work) as satisfied. The coordinated binding
handles a set step as it handles a delivery: a completed
order that carried fewer members than the task leaves the rest, a failed order is retried on the
members native still finds legal while attempts remain, and a binding is compatible only over a
non-empty subset of the task's own set.

### Prior steps

A selected task may carry `PriorSteps` (#3617): Work steps its order runs before `Step`, the same
task, relief and output. `CoordinatedActivityBinding.Start` and `Continue` write the order's steps
as the prior steps then the bound step, and a completed order's last step is the task's own. The one
user is a roof-holder's removal: `ConstructionMethodCandidates.RemovalBinding` carries a Deconstruct
op's `Cells` (the roofed cells the building leaves unsupported) as the candidate's `RoofCells`, and
`Start` turns them into the prior Work/RemoveRoof cell set step (generation names no step). Native's Deconstruct refusal on a roof-holder the enclosure
guard would wait on (`RoofWait`) is lifted when the same order's earlier RemoveRoof steps cover
exactly the cells `RoofSupportSafety.Unsupported` finds (a superset passes, for a continuation after
some are off); the guard still holds the Deconstruct job (`Eligible`, `GuardJob`) until that roof is
off, so nothing collapses. A prior step is validated like a step (Work only, no destination, recipe
or practice; a set step's rules). The native order sequence is one step after another, so between
the roof coming off and the wall coming down the game's own rules apply; that no pathological
geometry makes an intermediate cell removal collapse a neighbour is unverified.

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

The initial step families are exact thing-target work (Repair, Harvest, Research, Deconstruct and BeatFire through a named native WorkGiver; Research targets a research bench and works the current project, which the research Concern owns; Deconstruct targets a building the player may deconstruct, and the order stands its Deconstruct designation itself under the enclosure guard as an `Ordered` record, which the controlled pawn works under control authority alone where a Designate-stood record waits for supervised play; a roof-holder the guard would wait on is refused unless the order's earlier RemoveRoof steps take off the roof it leaves unsupported ([prior steps](#prior-steps), #3617); a designation the room reconcile stood by Designate is read back as a removal op and ordered the same way, and the order marks its record `Ordered`, #3557; Uninstall targets a minifiable building of the colony's own through `WorkGiver_Uninstall`, the order stands its Uninstall designation itself (the plain designation the reconcile's Pack stands) under an `Ordered` enclosure-guard record, and a roof-holder, a building designated for deconstruction or one with a reinstall blueprint is refused, #3612; CutPlant is the first [set step](#the-set-step), Work over a set of plants through `WorkGiver_PlantsCut`, the order standing each member's CutPlant designation, #3613; RemoveFloor is the cell set step, Work over a set of floored cells through `WorkGiver_ConstructRemoveFloor`, the order standing each member cell's RemoveFloor designation, #3615; Mine is the third, Work over a set of rock through `WorkGiver_Miner`, the order standing each member's Mine designation under the mine_safety guard, #3616; RemoveRoof is the second cell set step, Work over a set of roofed cells through `WorkGiver_RemoveRoof`, the order putting each member in the NoRoof area, #3617), selected food/count,
selected bed/rest, selected recreation building, exact delivery, exact cooking, material Supply, FinishFrame, Approach and Hold. Unsupported families are refused, not sent to a
native economic selector. Recreation without a building (walks, skygazing, social relaxing, drugs,
reading) remains outside this initial execution slice. Firefighting is an explicit policy-selected emergency revision, not an automatic native fallback. Work givers use forced:false, required capacities,
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
A delivered stack that carried a Haul designation (the room reconcile's HaulOut, read
back as a Haul removal op and ordered as this step, #3614) loses the designation on
the drop, as the native haul does on placing; the order marks any guarded record of
the designation `Ordered`.

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
deadlines never reset when revisited. A Hold step waits until its deadline or a
new revision; dependencies between steps require policy revisions. A deadline
without release fails visibly.
Sleep and recreation are bounded by their deadlines and may finish earlier through native recovery:
a sleep step succeeds once its pawn is awake at the native wake threshold, as the forecast's sleep ends.
An exact admitted recreation job ignores the legacy timetable's joy permission:
the Method owns its service window. Native joy gain, tolerance, target legality,
player authority and the absolute deadline still apply.
Native exhaustion sleep and the exact Vomit job may interrupt an assignment; the
interruption records failure and releases ownership before native physiology runs.
A completed or failed assignment awaiting continuation keeps its pawn waiting on
the hold job while the clock runs; retained ownership does not authorize other work.
Work deadline exhaustion is a failure. Native food/rest levels and physiological
consequences are not modified by execution.

Completion and failure never hold the clock (#3518) and publish no invalidation (#3556): an order finishing is
the brain's own attempt outcome, not an observed fact change, and the typed execution readback carries the terminal
state, which the brain reads at its next review. (The `pawn_activity` Pawns invalidation each once published selected
every pawn-reading planner at every review tick.) Native holds the clock for hazards and loss of authority only
([control loop](../architecture/control-loop.md#execute-under-supervision)).
Downing, mental state or drafting encountered at selection/start
fails and releases this voluntary-work owner; native involuntary behavior and
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
verify harvest, selected meal and selected bed. Each
`control/activity-*` case makes one lifecycle claim over the same installed
executions: batch atomicity, hold deadline, native work refusal, emergency
preemption of selected recreation, release, native overrides, idle completion,
player takeover and real save/load invalidation (`control/activity-reload`).
`control/work-resources` reads the installed ownership back through colony facts. Pure tests round-trip the
Hands action through the session codec and intent builder and reject malformed
batches. The activity observation case remains
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
| Recreation building | Enumerate each observed building a `JoyGiver_InteractBuilding` giver uses (horseshoes, hoopstone, chess, poker, Ur, billiards, televisions, telescope, instruments) that the giver's own `CanBeGivenTo` and `CanInteractWith` admit, with current tolerance-adjusted gain; Hands starts the giver's own play job on exactly that building. Future tolerance and playing-position travel remain unassessed. |
| No eligible target / exhausted bounds | No feasible candidate or explicit truncation, never an instant need or permission for native AI to choose. |
| Interrupted needs | Partial rest/recreation service only; unfinished eating contributes no forecast food. Reobserve before journal continuation/release; no replay credit. |
| Drafted / player-controlled / queued or player-forced work | No voluntary candidate. Existing player/combat authority owns the pawn; a timetable bypass cannot override it. |
| Downed / dead / mental state | No voluntary candidate or supported productive capacity. Native involuntary behavior and existing protection remain authoritative. |
| Unknown mechanics / occupied job duration | Unknown capacity, never free time. |
| Other recreation, DLC/mod needs, babies or special ingestion | Explicit unsupported effects/targets; no inferred adapter or hidden fallback. The blocker is a visible finding; the pawn's supported needs and labor stay selectable, and only incumbent retention excludes it ([control boundary](voluntary-control.md#control-boundary-and-unsupported-states), #3557). |

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

Active authority intercepts voluntary selection only for a controlled colonist
that holds a committed order: its selection, constant think-tree checks,
opportunistic work, finalizers and job starts are the order's. Existing exact
orders may finish; player work, drafted pawns, involuntary states and burning
retain their native or protective owner.

A controlled colonist with no committed order runs vanilla job selection (#3669,
replacing the explicit `Standby` order of #3662): every one of those entries
passes through, so the think tree's work, needs and joy givers run under the
colonist's work priorities, and designations, bills and zones with no coupled
binding are worked. When comparison retains no change for an available pawn, the
review installs nothing for it; there is no timed idle (the bounded 600-tick Idle
kind is gone) and no standby order. Hold remains solely an already-authorized
task dependency. An unowned colonist claims no pawn or target, so selected work,
eat or sleep acquires it from revision zero and interrupts its vanilla job. Work
candidacy and food/support availability treat it as free (`NativePawnActivities.Standing`).
The pawn read reports its real vanilla job with `JobEvidence.preemptible` set
(#3670): brain occupancy reads it as free, and target claims still see what it
holds, so a coupled order on that target is not refused for it. Changing authority leaves
an unowned colonist's vanilla job running; it only releases stale orders. Native
publishes nothing for an unowned colonist (#3556; the `unassigned_voluntary_pawn`
invalidation is gone), and the next rounds review reads it in the census. See [supported scope and remaining owners](voluntary-control.md).
Native disorientation and the native imposed-sleep/force-job ability effects retain
their direct job authority through exact source-method scopes. The scope applies only
to the affected pawn, restores on exceptions, and records interruption/release of any
voluntary assignment. It does not authorize arbitrary LayDown or Goto jobs.

Early native completion before the next selected task's departure installs that
task's bounded preparation under a new journal revision. The terminal old order
does not grant clock progress; preparation preserves the original deadline and
waits for the selected departure and observed prerequisites.

Selected range/sparring use the existing Work step with a typed practice mandate and reciprocal exact participants. See [assigned capability maintenance](capability-maintenance.md) for floor ownership, bounded execution and uncertainty. Native autonomous roster/skill selection is disabled under active bot authority for the current map; uncontrolled behavior remains native.
