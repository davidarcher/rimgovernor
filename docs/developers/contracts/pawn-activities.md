# Planner-owned pawn activities

Draft reconciliation reads outstanding journal actions and combat rosters. Applied
building intents do not hold a pawn drafted while native construction continues;
construction completion is assessed separately with its observed census. Pending
monument placement claims likewise read open marker moves, not unrelated building
work on the same plan.

## Finite native ordered work

`OrderedWork` uses the existing GiveJob intent and journal/Hands dispatch path.
It names a physical purpose and one to sixteen currently legal work-giver jobs in
execution order. It contains no future products or exclusive waiting steps.
Native requires an eligible colonist, no active baby care and an empty existing
queue before installing it. Each job gets vanilla reservations and is forced;
later jobs append with `EnqueueLast`. Native sustains no additional priority-work
target after this finite queue.

The saved latest sequence per pawn is the retry fence. The identical sequence and
request reads back exact native IDs/outcomes without issuing work again, including
after completion or reload. Altered, old or skipped sequences are refused. The
next sequence requires the previous jobs to have ended. Cancellation names the
same request and removes only its exact remaining job IDs, preserving unrelated
current and queued work. An observed successful end is `Succeeded`; a missing job
without that evidence is `Gone`. Dispatch acknowledgment is separate from these
physical outcomes. See [persistence ownership](persistence-contracts.md).

The shared work census includes ordered-work actions after acknowledgment. Its
claims use the full review's native queue observation: only remaining matching
job identities hold their targets, and their pawn remains claimed while any of
those jobs is live. Missing pawn rows, incomplete matching queue records or
same-tick evidence retain the known claims. A later observation of completed or
replaced jobs, or a positively read pawn without that queue, releases those claims;
it does not credit predicted output or certify the owning Need as met.

The registered `control/ordered-work-cancel-reload` and
`control/ordered-work-replaced-reload` cases verify this against actual saved
native jobs: partial completion survives load, cancellation preserves unrelated
current/queued jobs, and repeated requests cannot revive cancelled work. The
[labor evidence](../../requirements/colony-labor.md) records their scoped results.

The needs reader retains physically accessible service targets during forced or
queued work. The pawn still reports `player_work` and remains ineligible for
ordinary needs dispatch. Conditional overhead/layout estimates can read meal
mechanics without treating the current order as an absent food source or claiming
that vanilla will interrupt it.

Shared admission and retirement retain an observed active queue independently
of its dispatch acknowledgment. Shared comparison establishes intervention
justification
([native need response](forecast-contracts.md#current-native-need-response)), and
`StartCoupledActivity` binds exact orders only under this review's `Justified`
verdict. The adapter does not establish justification. Replacement and
cancellation decisions remain in the open labor migration. Routine portfolio
selection does not produce `OrderedWork` actions.

Native baby feeding and its associated safe return are occupied work. A standing
colonist doing that care is not preemptible, including while carrying the baby
after ingestion. Native admission rechecks care occupation for every productive
order, closing the race where feeding starts after the planning read. Exact
release cleanup remains admissible. Actual native takeover detaches the old
governor handle; vanilla's normal finalizer performs safe return.

`PawnActivities` is the existing Hands intent for atomic exact assignments. Its
Method journals selected tasks, prerequisite edges, participants, absolute departure
windows and remaining obligations. It has no separate scheduler or workflow store.
Food acquisition and material delivery/building use the same ColonyForecast search,
binding, journal continuation and native ownership contract.

Ordinary eating, rest and recreation allocations
are not installed, including inside productive portfolios. Their participant's
later work and transitive dependent tasks wait for fresh observed prerequisites;
the binding installs no future hold on their behalf. Already running native
service also prevents a modeled earlier task in that portfolio from interrupting
it now. Independent work remains selectable. Missing food access, blocked service,
an unread pawn or missing need-task metadata cannot supply an intervention
premise or leave an exact service eligible by default. Shared comparison retains detailed
conditional what-if evidence; `StartCoupledActivity` checks the current context
before binding. Access and current service are not proof of future recovery.
The needs read recognizes supported stationary native recreation without a
governor assignment. Matching ownership tags are optional observations of the
exact running governor job; native progress alone grants no release authority.
Existing exact commitments also hand ordinary service back at a task boundary,
including when their Need is unlisted. Independent running orders remain exact;
dependent attempts end without crediting the service as complete. See
[continuation](method-continuation.md#pawn-activity-methods). Exact orders that
remain after binding need an observed premise, as noted above. Productive caller
migration remains incomplete in the
[native-led labor change](../../../openspec/changes/native-led-labor-execution/design.md).

Exact cell assignments use `CellActivityStep`, which carries a required canonical
`Cell` destination. Deliver, Approach, BuildRoof and Sow require that form; other
steps cannot carry a destination. The candidate form follows the same distinction,
and binding, continuation, native execution and claim projection retain it. The
journal derives the form from the existing kind and job and stores a destination
only for cell steps. The journal codec also reads the former flat shape: a null
destination denotes a non-cell step, and a null task source denotes its stored
location. Exact destinations remain required on cell steps. Native wire JSON
preserves the concrete step type and requires exact cells.

Every selected task carries its exact source/approach location. It is the task's
location for stationary work and ordinary delivery; construction Supply names the
source stack separately from its site location. Preparation and continuation read
that source directly, without substituting a destination or a missing observation.

[Evaluated support work](support-work.md) retains conditional care, surgery and
demand-driven delivery scenarios. Routine execution belongs to native work;
those scenarios install no exact pawn claims. An old support Method returns
remaining work to native selection at a task boundary, preserving in-flight
work and independent participants and never crediting unfinished dependencies.
Critical medical orders retain their separate medical Need and existing action
contracts. Native self-service is governed by the boundaries below.

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

Harvest, raw delivery and eligible cooking remain conditional alternatives until
shared comparison. Routine harvest and raw-food delivery are marked for native
execution. Dispatch removes them and work depending on their unobserved completion;
it reserves neither the worker nor the future output. The food plan opens wild
plant and lone-hunt channels through native designations, while growing zones
provide native growers' work. Numbered priorities provide worker coverage.
Only actual observed output becomes usable stock. The explicit graph/binding
tests below exercise generic material continuation, not normal food dispatch.

## Live one-iteration cooking

Observed eligible existing bills offer bounded repeated supply/cook/output-delivery
alternatives through the same comparator and selected-task lifecycle. Native
reports exact slot counts, pawn-adjusted work, nominal nutrition and routes; it
neither chooses recipes nor creates bills. The current aggregate cooking route
supports observed participant and route alternatives. Shared composition can
combine partial native-certified stacks of the same ingredient definition.
Heterogeneous ingredient substitution and hypothetical benches without observed
bill intent remain unsupported.

Ordinary ingredient collection, cooking and output handling run through native
bills and work settings. These conditional graphs install no routine pawn order,
and no bill work is ever an exact order: the exact recipe primitive, its driver
and its fixture case were deleted (task 5.3 of `native-led-labor-execution`), and
a `Recipe` kind names a comparison binding only, refused as a step. A bill whose
closed short window showed pending or delivery-bound ingredients
(`ProductionStall`, IngredientsPending or DeliveryBound) re-admits only the
generator's ingredient delivery feeding it (the binding's `Feeds`), under a
`ProductionStall` purpose naming the product; a stall whose benches are exhausted
or whose work merely stays unstarted is refused `stall_not_answered_by_orders`.
Nominal nutrition is not eater-specific survival value.

## Live material delivery and building

The ordinary building planner considers sites from its existing Method history.
Native reads observe each site's material demand, eligible suppliers/builders,
source/return/site routes, carrying capacity and construction work. Policy builds
a supply prerequisite and finishing task, compares self-service with delegation,
and uses the same selected-task binding/continuation as food. Supply preparation
approaches the source; finishing preparation approaches the site. Native rebinds
blueprints to frames and reads remaining demand before each bounded trip. Actual
finished buildings, not accepted orders or successful supply, satisfy finishing.
A failed carrying step's job cleanup drops what it carried as a new stack; native
remembers that stack's id on the failed order and lists it as a Supply candidate, so
the continuation rebinds the carry to it (#3537). A frame's ended Supply is not the
shelter release boundary: the binding goes on to its finish or the carry's retry, and
only a finished site (FinishFrame or BuildRoof ended) releases the Method for review.
While a coupled order holds a site, an unowned colonist gets no vanilla delivery to it
(`ConstructionTierGate`) and no vanilla FinishFrame on it (`ConstructionSkillGuard`):
a blueprint carries no container reservation, so vanilla could otherwise turn it into
a frame under the order (#3537). The finisher read skips a colonist only while it
carries for a vanilla job that holds the stack (`CarryHeld`).

The live census supports one required material, repeated capacity-bounded trips,
or an already-supplied site. Native lists every spawned stack of the material the
player may use (#3046), each row with its `AvailableInput` and `Storage`, and its
`Carried` delivery priced for the share the stack can supply (the demand, or the
whole stack under it). Every stack that covers the demand alone is a source, each a
whole alternative; a remote one is a full-load Supply that collects nearby stacks
([remote construction pickup](spatial-contracts.md#remote-construction-pickup)),
by the rule native also applies to vanilla's delivery for a colonist with no
committed order.
When none does (#3673), the brain takes the stacks nearest the site first until
their sum covers it, as one set: every stack is taken whole but the largest (ties:
lowest id), which supplies the remainder. Native also reads, per pawn and placed
site's stack under the demand, the route from the site back to the stack
(`ConstructionActivityRead.Routes`). The brain delivers every stack of the set
before the finish. Too little usable stock leaves no row (`source_stock_short`); mixed-material
sites produce `mixed_material_demand_unsupported`. Arbitrary construction recipes
remain unimplemented. Reverse route
costs are observed separately; pickup/setup time and route changes remain estimate
limits. Native construction tiers, skill/capacity guards and reservations still
veto exact work. Existing construction demand owns the material budget.

## Live sowing

Ordinary sowing uses configured native growing zones/planters and work priorities.
The detailed sowing candidate is a conditional forecast with native execution;
selection installs no sow order, exclusive pawn claim or future holding. Its
physical step adapter below remains available to explicit bounded work and
existing Method reconciliation; its presence does not justify routine dispatch.

Sowing is a cell-targeted Work step through the grower sow giver (`WorkGiver_GrowerSow`), not a
new activity kind. Native reads, for every free colonist and every growing zone or planter with a
crop set, the cells the giver would accept now (crop set, cell free, fertility and season, adjacent
sow block, the pawn's Plants level against `sowMinSkill`, reservable and reachable), the pawn's
`PlantWorkSpeed`, the crop's sow work and the route to the nearest cell. Legality is the game's
own giver read through a private subclass that sets the giver's shared wanted-plant cache around
each call and accepts only a Sow job, so a cell the giver would answer with a cut-plant or
haul-aside job is not sowable and the tree-lattice patch applies. Policy proposes one task per
(pawn, zone) with as many cells as fit the horizon at that pawn's speed (`sowing_cells_truncated`
when it cuts); a zone is one exclusive outcome in this conditional projection, without
restricting native workers. The zone's crop is read, never chosen, and a zone with no crop
is reported (`sowing_zone_without_crop`). Project progress credits the forecast's cells;
only native observations establish actual sowing.

The step is `Destination` = the first cell, `Target` = the zone's (or planter's) unique load id and
`Count` = the cells to sow, validated like a BuildRoof step (a cell is required, and a positive
count). `JobDriver_PlantSow` sows one cell and ignores queued targets, so native offers one Sow job
per cell and keeps a cells-sown counter on the in-memory execution entry: the step stays current,
re-offering the nearest legal cell, until the count is sown. A step with no legal cell left ends
without a fault (a full zone is done); a load restarts the count, as it restarts the entry.
Continuation rebinds the nearest legal cell for the pawn and treats a fully sown grower as done.

## The set step

Standing CutPlant, RemoveFloor and Mine designations, Uninstall, and Deconstruct
are ordinary native work. Their conditional selected
tasks retain the target/set mechanics below but cannot install routine exact
orders. Existing commitments hand back at an observed boundary without crediting
completion or satisfying dependents. Planned preplacement cutting, mining and
deconstruction use the same boundary; reconciliation and excavation create the
native work. Roof preparation uses native area intent as described below.
Ordinary hauling is also native work; conditional delivery costs install no exact worker. Construction-clearance Deliver commitments hand back at an observed boundary, without crediting the uncarried remainder.

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

Native task reads can identify the members the pawn can still work (`Targets`
or `Cells`, `Count` their number) and positively report a set with no member
standing as satisfied. Coordinated continuation no longer retries routine set
work or replaces its set with a legal subset. An old failed set ends its attempt;
completion of only a physical subset grants no whole-task completion credit.

### Roof preparation

The clearance read exposes `ClearanceTarget.RoofRemoval`: proven removable cells
for standing demolition, empty cells with no roof prerequisite, or null when
preparation is unassessed/unproven. It reuses `RoofSupportSafety.Unsupported`;
fog, pending collapse, thick roofs and unproven geometry supply no option.
Policy uses only designated, non-dangerous targets and deduplicates their cells.
Those cells remain current clearance demand even when the building is already
designated, through the existing clearance Need, journal and Hands.

The action is ordinary `RemoveRoof` area intent. Vanilla chooses roof workers;
`RoofWait`, work-giver eligibility and completion-time guards continue to block
demolition until support is safe. Applied area intent is neither a removed roof
nor completed demolition. Conditional removal rows retain the combined work cost
without binding either worker. The obsolete task prior-step field and codec are
removed; primitive multi-step orders retain their existing native mechanics and
fixture coverage. Pathological intermediate flying-roof geometry remains a
native-mechanics proof limit of that primitive.

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
and `coordination/construction-delegated` prepared cases use normal Colony review and
Hands. Their fixture establishes facts, captures exact installed ownership and
injects one carrying failure; it installs no chosen tasks. They assert native
completion, physical output and observed cleanup. The construction case also compares native pawn comfort and safe-temperature minima with the selected shell comparison: both exposure inputs must be assessed, and the shell must reduce positive baseline `value.thermal` capability-days lost to hypothermia/heatstroke. `value.harm` is raid loss, so zero there does not imply absent cold injury. The staged -10 C temperature and worn apparel remain unchanged. Registration/compilation does
not establish that these game scenarios have passed; nightly execution supplies
that evidence. Pure and journal tests cover choice reversals, partial progress,
rebinding, unknowns, deadlines, refusal and stale atomic revisions.

### Intervention purposes and observed outcomes

Every non-release order of a `coupled-` Method (`PawnActivities.InterventionMethodPrefix`)
carries the `InterventionPurpose` its pawn's justified premise gave it; `Validate`
refuses one without a valid purpose, and the purpose is unchanged across
continuation revisions. Kinds and their end conditions, read only from
observations (never from elapsed ticks):

| Kind | Subject | Completed when | Cancelled when |
|---|---|---|---|
| `need_shortfall` | need, subject pawn | the pawn's need reads above its threshold | native service of that need is observed for a pawn the batch does not order |
| `food_risk` | `food` | the food owner reviews no response needed | no food consumer remains |
| `production_stall` | product | the reconciled calibration holds no stall of the product | the stall's cause is one orders cannot answer (any but `ingredients_pending` or `delivery_bound`) |
| `capability_practice` | requirement, subject pawn | the native baseline projects no missing XP | the requirement is withdrawn |
| `construction_stall` | placed site | the site's observed progress changed after the stall closed (its progress window opened after the evidence tick), or the site is no longer read | never; a worked site without progress stays open until the order's own deadline or failure |
| `unserved_care` | tend demand | the demand is no longer read (the patient needs no tend now) | never; a demand still read, served or not, stays open until the order's own deadline or failure |

`InterventionPurposes.AtContinuation` reads need shortfalls from the worker's
participant read (which also reads each purpose's subject pawn); the other kinds
stay open there. `AtReview` reads every kind from the review's context, and the
review releases an incumbent whose every purpose has ended whatever the selection
retains. Unread or unobserved evidence leaves a purpose open.

The observed outcome of one order (`InterventionPurposes.Observe`) is read from
the native execution row: `accepted` (installed or offered; no step started),
`running` (started, or a preparation holding), `completed`, `failed` (the owned
order's own condition, deadline or an involuntary state), `replaced` (another
owner's row, or the order's own failure with reason `native_job_takeover`) and
`released` (no native owner). Accepted is not completed, and a release or
replacement is never completed work; a failure or replacement ends the attempt.
The review classifies with the same `Observe`: `InterventionPurposes.ReviewOutcomes`
reads every installed `coupled-` Method's orders against the review's native
ownership rows (`WorkResourcePawn`, which carries the execution's `Method` and
stop `Reason` as the continuation read does, via `InterventionPurposes.Ownership`),
so a native job takeover reads `replaced` at both.

Admission refuses, as a `SharedAdmission` refusal before the claim overlap: a
stale prerequisite against the review's frozen evidence
(`stale_prerequisite:pawn_unavailable:<pawn>`, drafted, downed, in a mental
state or native care; `stale_prerequisite:input_consumed:<task>:<resource>`, an
observed stock input the evidence no longer holds); the same pawn and aim on an
open or installed plan (`intervention_already_open:<pawn>:<aim>`); and the same
pawn and aim on a plan retired at or after the candidate's evidence tick
(`stale_justification:<pawn>:<aim>`); every retirement path records
`plans.retired_tick`, so a purpose ended by the review's settled-plan retirement,
combat or load retirement is as stale as one continuation or resolution ended. Continuation ends an attempt whose next
task's target no longer binds as `stale_prerequisite:target_gone:<task>`. Inputs
are claimed once through the existing lot claims, so an open purpose never
debits stock twice.

**Self-service orders.** Ordinary meals, sleep and recreation are native: the
shared binding withholds them, and no routine Eat, Sleep or Recreation order is
installed. The one exception is a pawn's own service re-admitted under its own
`need_shortfall` of food, rest or joy (`InterventionRules.Readmitted`, from the
need observed unanswered at zero, [forecast](forecast-contracts.md)): it is bound
to its observed target at start, waits on nothing, and its deadline bounds it.
Continuation hands a pawn whose next task is a meal, sleep or recreation back to
native operation, so no self-service task is rebound later and none holds future
work exclusively. The order ends on its purpose (the need above threshold), a
native job takeover replaces and releases it, and the pawn returns to routine
native work holding nothing. `InterventionPurposes.Ended` cancels a batch holding
any other self-service shape (one whose purpose is not its own pawn's shortfall
of the need that service serves, including a preparation holding for one): a
routine order persisted before those were deleted is released, never continued
(`NativeServiceContinuationTests.AJustifiedSelfServiceEndsOnItsNeedAndAnOldShapeOneIsReleased`).
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

The initial step families are exact thing-target work (Repair, Harvest, Research, Deconstruct and BeatFire through a named native WorkGiver; Research targets a research bench and works the current project, which the research Concern owns; Deconstruct targets a building the player may deconstruct, and the order stands its Deconstruct designation itself under the enclosure guard as an `Ordered` record, which the controlled pawn works under control authority alone where a Designate-stood record waits for supervised play; a roof-holder the guard would wait on is refused unless the order's earlier RemoveRoof steps take off the roof it leaves unsupported ([roof preparation](#roof-preparation), #3617); a standing designation the room reconcile stood by Designate is ordinary native work; its read-back removal op remains conditional forecast evidence, #3557; Uninstall targets a minifiable building of the colony's own through `WorkGiver_Uninstall`, the order stands its Uninstall designation itself (the plain designation the reconcile's Pack stands) under an `Ordered` enclosure-guard record, and a roof-holder, a building designated for deconstruction or one with a reinstall blueprint is refused, #3612; CutPlant is the first [set step](#the-set-step), Work over a set of plants through `WorkGiver_PlantsCut`, the order standing each member's CutPlant designation, #3613; RemoveFloor is the cell set step, Work over a set of floored cells through `WorkGiver_ConstructRemoveFloor`, the order standing each member cell's RemoveFloor designation, #3615; Mine is the third, Work over a set of rock through `WorkGiver_Miner`, the order standing each member's Mine designation under the mine_safety guard, #3616; RemoveRoof is the second cell set step, Work over a set of roofed cells through `WorkGiver_RemoveRoof`, the order putting each member in the NoRoof area, #3617), selected food/count,
selected bed/rest, selected recreation building, exact delivery, material Supply, FinishFrame, Approach and Hold. Unsupported families are refused, not sent to a
native economic selector. Recreation without a building (walks, skygazing, social relaxing, drugs,
reading) remains outside this initial execution slice. No brain policy selects firefighting: vanilla fights home-area fires, and native releases committed orders for them ([home-area fire](#home-area-fire)). Work givers use forced:false, required capacities,
capabilities, native target checks and native reservations. They may still perform
the normal internal mechanics of their chosen job; this does not model every toil
as a separate economic decision.

`Deliver` names a currently spawned item, a positive count and an exact destination
cell. The custom delivery driver uses native pathing, reservations, stack splitting
and carrying, but does not ask the hauling WorkGiver to select storage. It never
collects nearby duplicates or substitutes a nearby drop location. Admission and
execution require a capable hauler, sufficient source quantity and carrying capacity,
an empty carry tracker or the exact selected carried stack, and a reachable destination that is
reservable by this pawn under vanilla's reservation rules and holds no loose item but one stack
the delivered thing joins with room for the count. One step is one trip: a count over the pawn's carry is refused.
A larger transfer is one task whose orders each carry one load (the coupled start clamps a Deliver to the binding's
`Capacity`); the continuation observes each result and repeats the step over the remainder, each later carry
landing on the earlier ones (#3625).
A delivered chunk loses its native Haul designation on drop, as native hauling does. Ordinary stacks have no separate haul-out record. Room placement uses native preview; construction workers haul blocking items aside. Exact Deliver remains a bounded primitive, not routine room execution.

Destination availability is rechecked during execution. A changed destination,
lost source, capacity shortfall or deadline fails the assignment visibly. Completion
requires the selected count dropped directly at the selected cell. An interruption
after pickup may leave food carried by the pawn: failure is not evidence that it is
still at the source, and a new planner decision must inspect that state. An exact carried stack can resume after fresh observed binding. Newly harvested/cooked items
require fresh observed identities and a revised assignment; future output IDs are
not guessed.

Surgery remains a `Work` step with job `DoBill` naming its existing medical bill;
that is the only step that carries bill arguments.

The requested first job is offered for an installed assignment. Once work runs,
native selection and overrides can choose replacements; constant think-tree,
opportunistic work and native finalizers run normally. Merely considering a job
does not revoke an assignment. An actually installed different job ends the old
execution handle, and shared continuation reconciles its remaining commitments.
Job completion does not force the bot's next step back over native selection.
Existing queued/player-forced work still refuses initial acquisition instead of
being erased. Remaining exact routine callers are tracked in the labor migration.

A selected native Job is identified by both reference and load ID, so pooling
cannot transfer ownership. Actual StartJob and EndCurrentJob hooks distinguish an
offer from execution. Admission and execution use native reservation checks for a
step's target, `Deliver` destination and `Supply` site. They never end another
holder's job to acquire its reservation. A native reservation failure terminates the attempt visibly
(`target_unreachable_or_reserved`).
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

### Home-area fire

Vanilla fights fires (#3662): a colonist with no committed order answers through
its Firefighter work priority, which the admin planner pins at 1. A committed
order would keep its colonist away until the order ends, so every 60 ticks
(a `TickManager.DoSingleTick` postfix) native checks the authority's map for
fires in the home area that are not attached to a pawn. It releases the order of
each committed colonist that is controlled, has Firefighting enabled and
active (the Pyromaniac trait disables it), is not already on `BeatFire`, is
not on an Eat or Sleep step, and stands within 15 cells of one. The reach is
vanilla's own firefighting radius (`WorkGiver_FightFires.NearbyPawnRadius`
and its reservation-check distance). When no committed colonist is in reach and no colonist is
beating a fire, native releases the nearest eligible colonist that can reach
one, so at least one answers. The release emits a `pawn_activity_execution` row
with state `released` and reason `home_fire`. The colonist then falls to
vanilla job selection like any unowned one. The brain reads the missing order at its next
review, and it may select work for the colonist again. The next check releases
the colonist again while the fire burns within reach. Fires outside
the home area and firebreaks are out of scope.

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
Colony review/Hands dispatch or autonomous recovery. Domain tests separately round-trip
the exact delivery payload through the journal codec and Hands intent builder.

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

## Ordinary tailoring

Clothing recipe, stuff, input allocation and delivery candidates remain conditional
forecasts. They do not install a pawn activity. Selected new production becomes
an unrestricted finite ordinary bill through the work-ledger Need, shared
material admission, journal and Hands. Existing bills and native unfinished work
create no duplicate request. Vanilla chooses the worker, gathers ingredients,
resumes unfinished garments and handles output. The receipt acknowledges bill
creation; only native observations establish actual production.

The private nonautonomous tailoring bill and exact tailoring executor have been
removed. Regenerate fixtures/saves containing that retired bill class. Old
journaled tailoring attempts return participants at task boundaries without
crediting unobserved work or starting dependent tasks. Routine cooking also stays
native, and no exact recipe primitive remains (task 5.3). See
[equipment upkeep](equipment-upkeep.md#live-clothing-requirement-selection).

## Voluntary ownership and bounded no-change

An installed order offers its first job; it does not own the pawn's future native
choices. Native replacement is observed after job installation using both job
reference and load ID. The old execution handle is detached without terminating
the replacement or its cleanup. Player work, drafting, involuntary states and
burning retain their native or protective behavior. This changes execution;
routine exact candidate migration and meaningful intervention admission remain
tracked in the labor change.

A controlled colonist with no committed order runs vanilla job selection (#3669,
replacing the explicit `Standby` order of #3662): every one of those entries
passes through, so the think tree's work, needs and joy givers run under the
colonist's work priorities, and designations, bills and zones with no coupled
binding are worked. When comparison retains no change for an available pawn, the
review installs nothing for it; there is no timed idle (the bounded 600-tick Idle
kind is gone) and no standby order. Hold remains solely an already-authorized
task dependency in intent; it offers no native waiting job. Native selection can
take over while that dependency waits. An unowned colonist claims no pawn or target, so selected work,
eat or sleep acquires it from revision zero and interrupts its vanilla job. Work
candidacy and food/support availability treat it as free (`NativePawnActivities.Standing`).
The pawn read reports its real vanilla job. Shared capacity counts useful work as
occupied and target claims protect what it holds, even when a direct order could
interrupt it. Ordinary service forecasts do not acquire an exact order through
this primitive; the shared binding projection excludes them. `Standing` permits native legality checks; it does not establish
free labor in the forecast. Changing authority leaves
an unowned colonist's vanilla job running; it only releases stale orders. Native
publishes nothing for an unowned colonist (#3556; the `unassigned_voluntary_pawn`
invalidation is gone), and the next colony review reads it in the census. See [supported scope and remaining owners](voluntary-control.md).
Native disorientation, imposed sleep and ordinary replacement jobs use the same
actual-job takeover observation; there are no source-method permission scopes.

Early native completion may journal preparation for a later task under a new
revision. It offers no indefinite native wait and cannot block native choice
until the selected departure. Any actual takeover is reconciled from fresh
ownership instead of reinstalling that future task over the replacement.

Selected range/sparring use the existing Work step with a typed practice (skill, partner, partner marker, duration; no role mandate since #3813) and reciprocal exact participants. See [assigned capability maintenance](capability-maintenance.md) for who practises, bounded execution and uncertainty. Native autonomous roster/skill selection is disabled under active bot authority for the current map; uncontrolled behavior remains native.
