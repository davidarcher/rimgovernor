# Control loop

[Architecture](overview.md) · [Controller contracts](../contracts/controller-contracts.md)

The controller observes needs, selects bounded work, advances supervised game time
and checks the outcome. Routine operation requires no model calls.

## Observe and prioritize

Native tools supply food, health, assignments, rooms, stock and threats. Sequential
reads may span world changes; missing information stays unknown. Forecasts help
choose work but cannot count projected harvests as stored food.

Emergencies preempt development. Food, wood and temperature use separate entry and
recovery thresholds to avoid replacing goals on small fluctuations. Goals retain
outcomes, methods retain approaches and steps identify executable work across reviews.

Optional projects (comfort, research, production targets, defense, expansion)
compete for bounded capacity based on measured deficits, player targets, waiting time,
labor contention and observed outdoor risk. Each goal declares the native work types
that can serve it; admission is bounded by both the project limit and free pawns of
those types. Accepted work keeps its identity as capacity changes; unavailable methods
yield to other candidates.
Optional-project admission is automatic (#655; the fixed-count
`--routine-project-limit` was removed in #875): it bounds slots only at eight
(planner cost) and admits every project a distinct observed worker can take
(`policy.DevelopmentCensus` matched by the worker allocator): a pawn enabled for
three work types is one worker, open startup and survival work holds its worker
without a slot, and open work beyond the census pauses new admissions
(`workers_overcommitted`) without cancelling it. The ranking and
method admission share one fit (`policy/development_capacity.go`): labor and the stage
are checked before the slot count, and admission refits against commitments
read inside its transaction, so a player project or another admission since the
ranking is counted. The development record shows the mode, workers held by startup
work, unused workers and the limiting reason. A goal waiting on a measured shortfall lends its ordering to the goal that acquires it 
(`policy/development_dependency.go`, #651): a shelter shell admitted short of a resource 
records a typed edge (goal epoch, method, each open action's cost per resource, the stock it 
was measured against), and while the open costs exceed current stock MaintainResource 
ranks ahead of unrelated optional work for the next slot and worker. The donation is 
ordering only: the declared priority, the startup/emergency classes and the clock are 
unchanged, an explicit project limit still holds (the row reports `project_limit`), 
shared actions count once, and the edge drops when its actions settle, the goal epoch 
changes, the world changes or a day passes. Cycles, chains past four goals, unknown 
stock and a prerequisite without an executable method donate nothing and are listed 
as blockers on the development record. Work displays the reason for deferral, including the
bottleneck work type. Worker capacity is a scheduling bound, not a completion-time
guarantee, and waiting age alone overtakes any deficit gap within a fixed tick bound.
Accepted work holds its slot only while it is worked: the review reads each pawn's
current job and the work type of the giver that issued it, and a commitment whose
profile no pawn is on, while a pawn enabled for it idles or works for another type,
releases its slot after a game hour of that (`labor_idle`) without closing the work;
a pawn back on it takes the slot back. A colony asleep is no evidence either way.

Disease care projects game days to lethal severity and full immunity from the
native per-day rates. When immunity loses or leads by less than one day, the
work planner enables Patient and bed rest at highest priority and disables
other work for that pawn. Checkbox mode enables only those rest work types.
The durable medical history holds rest until every triggering disease is
observed immune or absent from a complete census; missing reads and improved
forecasts do not release it. World replacement and tick rewind reset the hold.
On recovery, normal roster allocation resumes, including saved player work
preferences. The temporary rest hold does not rewrite those preferences.

RimWorld selects a reachable medical bed for medical rest, falling back to the
pawn's ordinary bed under native rules. Medical beds cannot be assigned by the
bed-ownership operation. The hospital planner supplies medical beds, and the
sleeping planner uses the existing bed-assignment operation for ordinary beds.

## Goal concepts

Every goal the routine review raises is one of four concepts (epic #1012).
Today all four are still stored as goal rows; the concept says which
lifecycle a goal follows as they move apart.

| Concept | What it is | Lifecycle |
| --- | --- | --- |
| Standard | A measured target held over time. A chore is a Standard whose target is no outstanding work. | Keyed by world and GoalID; re-arms with a new epoch when the target regresses. |
| Project | A finite piece of work with a finished state and dependency links to other Projects. | Keyed by world and GoalID; finishes once. A finished Project that later breaks opens a new Project, never an epoch bump. The colony stage is derived from finished foothold Projects. |
| Response | An incident triggered by an event, one row per occurrence (trigger, start, end). | Opens on the event, closes when it is handled. Methods and plans still go through the shared ColonyPlan and Admission. |
| Rule | An admission veto. It rejects proposals; it pursues nothing and owns no methods. | Evaluated at Admission. Suspending other work is a Rule's job, not a priority value. |

Rules carry no GoalID of their own: the emergency check (`EmergencyRule`) and
the unsafe-item veto split from `ManageSupplySafety` are Rules, while
`ManageSupplySafety` itself is the Standard doing the allow and forbid work.

Every GoalID in `go/internal/policy`:

| Concept | GoalIDs |
| --- | --- |
| Response | `ActiveCombat`, `CriticalMedicine` (`CriticalMedical`), `RestoreWorkers`, `MoodGoal(pawn)`, `AnswerDialog`, `ConfirmColonyNames`, `RecoverDisasterServices`, `TradeWithCaravan` |
| Project | `AllowStartingSupplies`, `EnsureCooking`, `MaintainButcherSpot`, `EnsureBasicPower`, `EnsureWorkAssignments`, `EnsureResearch`, `EnsureDefensiveLayout`, `ClearAncientShrine` |
| Standard (chore) | `MaintainWaste`, `RemoveBlight`, `MaintainStockpiles`, `TidyLayout`, `ClearHomeObstructions` |
| Standard | `EnsureFoodSupply`, `EnsureBasicDefense`, `EnsureTemperatureSafety`, `EnsureComfort`, `MaintainHousing`, `ManageSupplySafety`, `SecureSupplies`, `ClearPests`, `MaintainAnimalContainment`, `MaintainAnimalFeed`, `MaintainCleanFacilities`, `MaintainEquipment`, `MaintainEssentialRepairs`, `MaintainFireSafety`, `MaintainFirebreak`, `MaintainFlooring`, `MaintainFoodStorage`, `MaintainHerd`, `MaintainHomeCoverage`, `MaintainLighting`, `MaintainMedicalReserves`, `MaintainSurgery`, `MaintainPopulation`, `MaintainPermits`, `MaintainPsylink`, `MaintainRefrigeration`, `MaintainResource`, `MaintainRoutes`, `MaintainStoneShell`, `MaintainStorage` |
| Rule | none (see above) |

`policy.GoalConcept` returns this classification, and a test fails on any
unclassified GoalID. The foothold goals are Projects, except `EnsureFoodSupply`
and `EnsureBasicDefense`: food days and armed colonists are measured targets
held over time, so they are Standards. `TradeWithCaravan` handles a caravan
arrival, so it is a Response.

A second axis, the domain, tags every GoalID with the colony area it serves,
like a Civ advisor. `policy.GoalDomain` returns it and the same test fails on
any untagged GoalID. A domain only groups goals in panels; it never ranks goals
or budgets labor.

| Domain | GoalIDs |
| --- | --- |
| Food | `EnsureFoodSupply`, `EnsureCooking`, `MaintainButcherSpot`, `MaintainFoodStorage`, `MaintainRefrigeration`, `RemoveBlight` |
| Shelter | `EnsureInitialShelter`, `EnsureBasicComfort`, `EnsureComfort`, `EnsureTemperatureSafety`, `EnsureExpansion`, `MaintainSleeping`, `MaintainStoneShell`, `MaintainLighting`, `MaintainFlooring`, `MaintainHomeCoverage` |
| Industry | `EnsureBasicPower`, `MaintainResource`, `EnsureResearch` |
| Military | `ActiveCombat`, `EnsureBasicDefense`, `EnsureDefensiveLayout`, `ClearAncientShrine`, `ClearPests`, `MaintainEquipment` |
| Medical | `CriticalMedicine`, `MaintainMedicalCare`, `MaintainMedicalReserves`, `MaintainSurgery` |
| People | `RestoreWorkers`, `EnsureWorkAssignments`, `MaintainPopulation`, `MaintainPsylink`, `MaintainPermits`, `MoodGoal(pawn)`, `MaintainHerd`, `MaintainAnimalFeed`, `MaintainAnimalContainment` |
| Upkeep | `AllowStartingSupplies`, `SecureSupplies`, `ManageSupplySafety`, `MaintainStockpiles`, `MaintainStorage`, `TradeWithCaravan`, `MaintainWaste`, `TidyLayout`, `ClearHomeObstructions`, `MaintainCleanFacilities`, `MaintainEssentialRepairs`, `MaintainFireSafety`, `MaintainFirebreak`, `MaintainRoutes`, `RecoverDisasterServices` |
| System (no panel section) | `AnswerDialog`, `ConfirmColonyNames` |

## Execute under supervision

Execution uses bounded native tick windows and a renewable wall-clock
lease. Danger and player input can stop a window
early (the stop tier, #240); an injury stops it only past the native
severity floor -- a life-threatening stage or a bleed-out inside two
in-game hours -- because a lighter wound and a discharged rest watch buy
the same medical review through a journal wake, without the
stop-to-readmit pause (#584). Lease expiry also stops a controller that
becomes unresponsive. Reviews and routine orders happen at the stop between
windows and under a running window alike (#243, #244): the planners read
one tick-consistent bundle and bind their facts to its tick, and the worker
dispatches every routine kind live; only the window itself is admitted at
the stop.

The scheduler runs independently of dashboard refreshes. Only one review or
execution task runs at a time. Hands yields at its operation budget and requests
continuation. Idle/blocked work and autosave refusals use a two-second retry
backoff; lease renewal and periodic observation remain independent of task
completion.

Delivery from the native clock is a poll on the event journal, not a push:
the transport is request/response only, so the poll's `mirror_poll`
(the scope and the events page in one call, like `clock_read_events`) can hold an
empty read for up to `wait_ms` (at most 5 s) and answer as soon as a row lands.
The service holds the read while a window it admitted is running (4 s, the
`serve` bound), so a stop is seen as soon as its row lands; between windows,
the poll waits locally for scheduler step completion, then reads immediately.
The poll interval remains a safety bound so a blocked step cannot hide player
input or authority interruptions. The local wait does not occupy the native
transport while the paused step needs reads.
A running held read that returns empty early still falls back to its poll
cadence. A committed stop warms the pawn and emergency admission observations
before waking the step. The step reuses them only at the same paused tick,
identity and native generation, within its freshness bound and with no cache
invalidation since the warm read; otherwise it reads them again. Every
captured page wakes the scheduler step and the routine worker through their
wake signals, which also reset the step backoff; a page whose events carry
attempt outcomes names those actions so the worker reconciles them first, and
while any named action is still unreconciled the worker steps again at once
instead of waiting out its step interval. A
window can be armed with watched attempts: the native supervisor stops it at the
tick boundary on which any of them reaches a terminal outcome
(`STOP_REASON_WATCH_LATCHED`, a benign stop like the tick budget). The
scheduler arms them for a combat window only, the dispatched construction
and haul attempts of the window (the families whose native operation
records observe their own terminal outcome, at most 16), so the fight's
next step starts at the outcome tick. A routine window watches nothing
(#244): a completed order is not a reason to stop the clock, the
`OperationOutcome` row the poll carries wakes the worker under the running
window, and the step records the dispatched attempts the window does not
watch (`unwatched` on the `clock_step` row) as evidence.
A coupled order, a plan action written against what an earlier action in
the same plan produced (`ActionDependency.Coupled`), stops nothing either
(#584): the prerequisite's `OperationOutcome` row is the wake, the step
that sees the order ready plans live for it ahead of the planner wave's own
cadence, and the CAS evidence its admission carries is what refuses an
order whose read the world has left behind. The step row names the orders
(`coupled_orders`).
Authority
changes observed while no epoch is running are journaled as
owner-less `AuthorityChanged` rows so a waiting poll learns of them at once.

Each scheduler step carries the reason it ran, and the reason selects the
planners: a step after a settled window, a tick advance or the 30 s safety
net plans everything; a timer step at the same paused tick runs no planner
and only re-evaluates admission from the journal; a wake runs the planners
that dispatch the latched outcomes' action kinds and the readers of any
invalidated fact family (an authority change plans everything); a step
that finds its own window running plans `live` (a full or wake step at
once, a timer step when the safety net is due) and admits nothing. Planner
facts are bound to the tick they observed, so admission holds with
`stale_planning` when they predate the admitted tick by more than the
planning tolerance (`bridge.PlanningTickTolerance`, the tightest fact
family's: 250 ticks) or a window has since outrun them; the scheduler's
`MaxAge` bounds only the admission reads. A routine window runs
one game day (60000 ticks, the review guarantee of #126; fixed since #875) unless danger or player
input stops it earlier: there is no wall-time budget and no `--clock-window-seconds` any
more (#244), since reviews and routine orders happen under the running
window. Each step's flight-recorder `clock_step` row carries the window it
admitted, the reason the step acted on and, for a step a clock stop woke,
the latency from the native stop stamp to the step (`stop_latency_ms`,
#112), which `rimgovernor phases` reports as steps by reason and
stop-to-step latency. Combat windows stay at 300 ticks; a native work
allowance (a growing field, a home fire) still clamps either.

## Manual control

Manual mode is the only thing that pauses controller action. While
`NativeControlAuthority` reads Manual (for example the player took over
during combat), the controller does nothing. Once it reads Auto again the
controller may act on anything on the map immediately, including something
the player just drafted, forced, restricted or placed: there is no
per-subsystem "player owns this, hands off" state and no waiting period.
Squad, rescue, tend, repair and recovery selection prefer candidates without
forced or queued work, but that evidence never excludes the remaining candidates.
Native job legality and exact snapshot checks still apply.

Drafts are plan-owned (#939): there is no native draft claim. A plan drafts
the pawns it needs through the draft intent, and the census-based undraft
sweep undrafts every drafted colonist no live plan needs (an unsettled or
still-held draft action, the capturer of an open capture or arrest plan,
or an open fight's roster), sparing a pawn native is running an Arrest or
Capture job for. RestoreWorkers stands while such a stray draft waits for
the sweep.
`TestIdleDraftObservationGuards` (buildingruntime) covers the candidate
guards and admission; the colony snapshots in `internal/snapshot`
(`draft_idle_test.go`, recorded from the former `draft/idle` and
`draft/idle-hostile` cases) replay the peaceful RestoreWorkers and the
threatened ActiveCombat review.

A squad hunt is the `ActiveCombat` incident's hunt origin: while no hostile
stands and the food plan opens a squad `Hunt` channel (a group of three or
more wild animals, or any animal a lone hunter must not designate, with
three ranged colonists able to form the squad), the review asserts the
deficit with the channel's prey as the occurrence's payload
(`store.HuntPrey`). The fight then runs through `admitFight` with
`CombatView.Hunt` set and the prey as its threats; the frame's detail rows
cover the open hunt census rows. A hostile in the frame, or the plan no
longer opening the hunt, ends the origin. An open hunt fight runs in combat
windows with the live prey as the window's acknowledged ids
(`ClockWindowFacts.HuntPrey`), so the combat budget backstop wakes its next
decision; no hostile exists to make the window a combat one otherwise.

An outmatched fight calls the royal permits its colonists hold (#1608,
`combat_permit.go`): with the royalty read (`CombatView.Royalty`, the
review's `RoutineFacts.Royalty`) known, a held acting aid or strike permit
that is off cooldown and affordable (favor at least its cost) yields a
`permit_call` order, once per holder and permit per fight
(`CombatMemory.Permitted`). Aid lands on the defender nearest the squad's
centre; a strike lands on the densest hostile clump no colonist stands
within the mortar safe radius of. Unread favor, cooldown or royalty facts
hold the call. The order is no `combat.orders` entry: the defense planner
commits it as an `ability` action (permit source, cell target) on its own
incident method, and the fight's stops wait while it is open. The admission
stop makes no call.

A psycaster casts its known combat psycasts in a fight (#1611,
`combat_cast.go`): with the royalty read known and a live hostile, each
standing colonist yields at most one `psycast_cast` order per stop, the
ready psycast of the highest family (heal, then stun, burst, defensive). A
psycast is ready when its psyfocus cost fits the caster's psyfocus, its neural
heat fits under the ceiling and its cooldown has run out, all read from the
royalty read (`PsycasterState`, `Psycast.CooldownRemaining`); an unread fact
holds the cast. The read is slow, so each cast is remembered for the fight
(`CombatMemory.Casts`): its cooldown, psyfocus cost and heat still count
against the caster until the fight ends. The read carries no effect
category, so `psycastFamilies` classifies by vanilla def name and never casts
an unlisted def. The target is the arm the ability takes (its target kind),
within reach of the caster: heal a defender under 60% health, stun and
pawn-targeted burst the nearest hostile, area burst the densest hostile clump
no colonist stands near (the strike rule), defensive the caster (a self-cast,
or the caster as the pawn target) while a hostile is in reach. The order is no
`combat.orders` entry: it commits as an `ability` action (psycast source) on
the permit calls' incident method, native owning the guards.

Colony, load and map changes and stale in-flight snapshots still invalidate
pending work; that is ordinary concurrency safety, not a player-ownership
rule. A pause or letter pause only suspends routine goals and their open
work until control resumes in the same world (see the
[overview](overview.md)): the drafts a suspended plan still holds
(a completed draft with unfinished, unfailed work behind it, such as a
combat hold plan's defenders) stay needed through the hold and the resume,
and the next order's drafted check refuses a pawn the player undrafted
meanwhile (`not_drafted`). An explicit Pause undrafts nobody: drafted pawns
stay drafted, and with authority inactive the game's own auto-undraft
applies again. The game's own
pause on an informational letter (NeutralEvent, PositiveEvent,
NegativeEvent, the classes the native supervisor never stops play for)
stops the window but holds nothing: the next step admits again without a
resume (#228).

## Auto takeover acceptance

A player's Manual edit is an ordinary deficit to Auto, never a provenance
hold. Recorded colony snapshots (`internal/snapshot`, taken from the retired
`takeover/*` cases, #748) replay the review over each staged edit: an edited
timetable or a restrictive saved diet reads as a work deficit
(`EnsureWorkAssignments`), a removed Home cell opens `MaintainHomeCoverage`,
saved allowed-area restrictions are cleared, a suspended feed bill and standing
release/slaughter flags open their upkeep goals. `takeover/draft` still adopts
and releases a standing player draft natively.
Resource and animal-feed production replace an inactive bill for the selected
recipe using its native identity and the current bench snapshot. An active bill
continues to suppress duplicate production; unrelated recipes remain unchanged.
The diet planner restores missing natively eligible definitions while retaining
ingredient filters and condition ranges; it never changes a shared diet in place
or forces a pawn to eat.
`draft/intent` checks draft, same-state and undraft readbacks;
`upkeep/home-coverage` checks connected Home restoration and save recovery.
Obsolete standing release/slaughter designations are cancelled through shared
Hands. Destructive orders still require explicit opt-ins and native eligibility.
A standing demolition order is neither a hold nor a ledger entry: an explicit
removal adopts it, and a designation alone does not create a controller plan need.

## Verify progress

Hands records receipts; completion tracking checks native postconditions. A
no-progress watchdog can hold stalled work. Observed completion can release that
hold without replacing the action or its evidence.

Stability requires sleeping capacity, shelter, food, production, storage, cooking,
temperature and other gates together. Changed conditions can invalidate stability.
Sustained coverage belongs in campaigns and
[GitHub issues](https://github.com/davidarcher/rimgovernor/issues).

The gated [Go routine components](../../../go/README.md#routine-policy-components)
persist maintained goals, method reservations and action dependencies. Building
methods reserve all costs atomically against the shared journal; Hands rechecks
native placement and observed predecessor completion before execution. Runtime
composition for additional methods and method selection remain tracked in G01.05. Go routine reviews
persist need assessments and hysteresis together; missing facts cannot recover
goals. Manual cancels pending work independently of observation availability.

The opt-in Go routine building worker shares the selected player's direction and
native lease. Its journal verifies each method's current review, goal, epoch and
world before dispatch; it cannot run arbitrary plans or acquire authority. Pending
player work takes priority. Routine building work can keep finite clock windows
eligible after the selected player plan settles. Uncertain effects still reconcile
after cancellation, while observed terminal building outcomes yield accounting to
fresh native stock and placement facts.
A worker step previews its building candidates in one native batch and each
candidate's first inspection takes its preview from that memo, reading the map
bounds and the emergency state in parallel; the inspection after durable
preparation always previews live (#593).

Comfort joins that shared path through native access and use observations. Its
durable history distinguishes completed furniture from ordinary dining and
recreation use. After construction, a bounded clock allowance lets pawns use the
facilities; it expires from the original completion tick and cannot renew through
polling or transfer to a new player direction.
