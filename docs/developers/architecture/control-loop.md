# Control loop

[Architecture](overview.md) · [Controller contracts](../contracts/controller-contracts.md)

The controller observes needs, selects bounded work, advances supervised game time
and checks the outcome. Routine operation requires no model calls.

## Observe and prioritize

Native tools supply food, health, assignments, rooms, stock and threats. Sequential
reads may span world changes; missing information stays unknown. Forecasts help
choose work but cannot count projected harvests as stored food.

Emergencies preempt development. Food, wood and temperature use separate entry and
recovery thresholds to avoid replacing Standards on small fluctuations. Concerns retain
outcomes, methods retain approaches and steps identify executable work across reviews.

### Optional-project admission

Optional projects (comfort, research, production targets, defense, expansion)
compete for bounded capacity based on measured deficits, player targets, waiting time,
labor contention and observed outdoor risk. Each Concern declares the native work types
that can serve it. Accepted work keeps its identity as capacity changes; unavailable
methods yield to other candidates.

- Admission is automatic. Slots are bounded only at eight (planner cost); every
  project a distinct observed worker can take is admitted (a pawn enabled for
  three work types is one worker).
- Open startup and survival work holds its worker without a slot. Open work beyond
  the census pauses new admissions (`workers_overcommitted`) without cancelling it.
- Ranking and method admission share one fit (`policy/development_capacity.go`):
  labor and the stage are checked before the slot count, and admission refits
  against commitments read inside its transaction, so a player project or another
  admission since the ranking is counted.
- The development record shows the mode, workers held by startup work, the limiting
  reason and, per deferred project, the reason (including the bottleneck work type).
  Worker capacity is a scheduling bound, not a completion-time guarantee; waiting
  age alone overtakes any deficit gap within a fixed tick bound.
- Accepted work holds its slot only while it is worked. The review reads each pawn's
  current job and the work type of the giver that issued it. A commitment whose
  profile no pawn is on, while a pawn enabled for it idles or works for another
  type, releases its slot after a game hour (`labor_idle`) without closing the work;
  a pawn back on it takes the slot back. A colony asleep is no evidence either way.

**Dependency donation** (`policy/development_dependency.go`). A Concern waiting on a
measured shortfall lends its ordering to the Concern that acquires it: a shelter
shell admitted short of a resource records a typed edge (Episode, Method, each open
action's cost per resource, the stock it was measured against). While the open costs
exceed current stock, MaintainResource ranks ahead of unrelated optional work for
the next slot and worker.

- The donation is ordering only: declared priority, the startup/emergency classes and
  the clock are unchanged, an explicit project limit still holds (the row reports
  `project_limit`) and shared actions count once.
- The edge drops when its actions settle, the Episode changes, the world changes or
  a day passes.
- Cycles, chains past four Concerns, unknown stock and a prerequisite without an
  executable method donate nothing and are listed as blockers on the development
  record.

### Disease care

Disease care projects game days to lethal severity and full immunity from the
native per-day rates. When immunity loses or leads by less than one day, the
work planner enables Patient and bed rest at highest priority and disables
other work for that pawn. Checkbox mode enables only those rest work types.

- The durable medical history holds rest until every triggering disease is
  observed immune or absent from a complete census; missing reads and improved
  forecasts do not release it. World replacement and tick rewind reset the hold.
- On recovery, normal roster allocation resumes, including saved player work
  preferences. The temporary hold never rewrites those preferences.
- RimWorld selects a reachable medical bed for medical rest, falling back to the
  pawn's ordinary bed under native rules. Medical beds cannot be assigned by the
  bed-ownership operation. The hospital planner supplies them (the storage planner
  sites their medicine zone); the sleeping planner assigns ordinary beds.

## Concerns and their forms

The governor makes **Rounds**: in each **Department** it runs an **Inspection** on
every **Concern**. A Concern is a kind the governor watches (`EnsureFoodSupply`,
`ActiveCombat`) and takes one of three **Types**, which says which lifecycle it
follows. **Safeguards** veto unsafe Plans at Admission, and the chosen **Method**
produces a **Plan**. Shared vocabulary is in the
[glossary](../agent-runbook.md#vocabulary-glossary-epic-1964); code and storage
still carry some old words until the rename children of #1964 land, so grep for
`Rounds` to find Rounds.

Storage:

- A Standard is a `standards` row; a Project is a `projects` row; an Incident is an
  `incidents` row. No Project or Incident kind is ever a `standards` row.
- Methods live in `standard_methods`, `project_methods` and `incident_methods`,
  tied together by `plan_owner(plan_id, kind)` so a plan has one owner; the
  read-only `plan_methods` view serves plan-to-owner lookups. All go through the
  same admission. A Project or Incident row has no episode.
- A Project is saved as `project/<id>` (`GovernorProjectBlob`,
  `GovernorStateSchemaVersion` 3); its methods are re-planned after a load.
- Rounds bind Projects separately from Standards (`Rounds.Projects`,
  `RoundsResult.Projects`): one row per world and kind, reused while it stands,
  invalidated with the world, and replaced by a new row when a Completed Project is
  measured broken with no work open. A player activation that forces a deficit on a
  Completed Project mints a new `project-player-<hex32>-<kind>` row the same way.

| Type | What it is | Lifecycle |
| --- | --- | --- |
| Standard | A measured target held over time. A chore is a Standard whose target is no outstanding work. | Keyed by world and Concern; its Inspection finds it Met, Unmet or Unclear. Rows are Open, Settled or Voided; it starts a new Episode (today `epoch`) when a settled target goes unmet again. |
| Project | A finite piece of work with a finished state and dependency links to other Projects. | One `projects` row per `project-<hex8 world digest>-<kind>-<gen>` id (`domain.Project`, no episode); Open, then Completed (or Voided with the world). A Completed Project that later breaks opens a new Project, never an Episode. The colony stage is derived from Completed foothold Projects. |
| Incident | An occurrence triggered by an event, one row per occurrence (trigger, start, end). | Its Inspection reports a Situation: Active, Clear or Unclear. Opens on Active, closes on Clear. "Response" is prose only, for the Methods and Plan chosen for an Incident; they still go through the shared ColonyPlan and Admission. |

A Safeguard is not a Type. It is an admission veto: it rejects proposals,
pursues nothing and owns no Methods. It is evaluated at Admission, and
suspending other work is a Safeguard's job, not a priority value. Safeguards
carry no Concern id of their own: the emergency check (`EmergencySafeguard`) and
the unsafe-item veto split from `ManageSupplySafety` are Safeguards, while
`ManageSupplySafety` itself is the Standard doing the allow and forbid work.

Every Concern id in `go/internal/policy` (`ConcernID` in code):

| Type | Concern ids |
| --- | --- |
| Incident | `ActiveCombat`, `CriticalMedicine` (`CriticalMedical`), `RestoreWorkers`, `MoodConcern(pawn)`, `AnswerDialog`, `ConfirmColonyNames`, `RecoverDisasterServices`, `TradeWithCaravan` |
| Project | `EnsureCooking`, `MaintainButcherSpot`, `EnsureBasicPower`, `EnsureWorkAssignments`, `EnsureResearch`, `EnsureDefensiveLayout`, `ClearAncientShrine` |
| Standard (chore) | `MaintainBurial`, `MaintainWaste`, `MaintainIncineration`, `RemoveBlight`, `ManagePollution`, `EnsureMechCharger`, `MaintainGeneBank`, `MaintainStockpiles`, `ClearHomeObstructions` |
| Standard | `EnsureFoodSupply`, `EnsureBasicDefense`, `EnsureTemperatureSafety`, `EnsureComfort`, `MaintainHousing`, `ManageSupplySafety`, `ClearPests`, `MaintainAnimalContainment`, `MaintainAnimalFeed`, `MaintainBabyFeeding`, `MaintainCleanFacilities`, `MaintainEquipment`, `MaintainEssentialRepairs`, `MaintainFireSafety`, `MaintainFirebreak`, `MaintainFlooring`, `MaintainFoodStorage`, `MaintainHerd`, `MaintainHomeCoverage`, `MaintainLighting`, `MaintainMechs`, `MaintainMedicalReserves`, `MaintainSurgery`, `MaintainPopulation`, `MaintainPermits`, `MaintainPsylink`, `ManageCreepJoiners`, `MaintainIdeoRoles`, `MaintainRituals`, `MaintainRefrigeration`, `MaintainResource`, `MaintainRoutes`, `MaintainStoneShell` |
| Safeguard | none (see above) |

`policy.ConcernTypeOf` returns this classification, and a test fails on any
unclassified Concern id. The foothold Concerns are Projects, except `EnsureFoodSupply`
and `EnsureBasicDefense`: food days and armed colonists are measured targets
held over time, so they are Standards. `TradeWithCaravan` handles a caravan
arrival, so it is an Incident.

A second axis, the Department, tags every Concern with the colony area it
serves. `policy.DepartmentOf` returns it and the same test fails on any untagged
Concern id. A Department only groups Concerns in the launcher; it never ranks
Concerns or budgets labor.

| Department | Concern ids |
| --- | --- |
| Food | `EnsureFoodSupply`, `EnsureCooking`, `MaintainButcherSpot`, `MaintainFoodStorage`, `MaintainRefrigeration`, `RemoveBlight` |
| Shelter | `EnsureInitialShelter`, `EnsureBasicComfort`, `EnsureComfort`, `EnsureTemperatureSafety`, `EnsureExpansion`, `MaintainSleeping`, `MaintainStoneShell`, `MaintainLighting`, `MaintainFlooring`, `MaintainHomeCoverage`, `MaintainFireSafety`, `MaintainFirebreak`, `MaintainEssentialRepairs`, `MaintainRoutes`, `ClearHomeObstructions`, `RecoverDisasterServices` |
| Industry | `EnsureBasicPower`, `MaintainResource`, `EnsureResearch`, `MaintainMechs`, `TradeWithCaravan`, `EnsureMechCharger` |
| Military | `ActiveCombat`, `EnsureBasicDefense`, `EnsureDefensiveLayout`, `ClearAncientShrine`, `ClearPests`, `MaintainEquipment` |
| Medical | `CriticalMedicine`, `MaintainMedicalCare`, `MaintainMedicalReserves`, `MaintainSurgery`, `MaintainGeneBank` |
| People | `RestoreWorkers`, `EnsureWorkAssignments`, `MaintainPopulation`, `MaintainPsylink`, `ManageCreepJoiners`, `MaintainPermits`, `MaintainIdeoRoles`, `MaintainRituals`, `MaintainBurial`, `MoodConcern(pawn)`, `MaintainHerd`, `MaintainAnimalFeed`, `MaintainAnimalContainment` |
| Storage | `MaintainStockpiles`, `ManageSupplySafety` |
| Sanitation | `MaintainCleanFacilities`, `ManagePollution`, `MaintainWaste`, `MaintainIncineration` |
| System (no panel section) | `AnswerDialog`, `ConfirmColonyNames` |

A Department that owns stockpiles is also an entity (`policy.StoreOwner`,
registered in `storeOwners`): it declares its `Stores` (a `policy.Store`: role,
planned room or rectangle, filter, priority, and the room it asks for when full)
and its `RoomDemand` from capacity (`DeclaredDemand`). `MaintainStockpiles` is
the one applier: it creates a declared store's zone, retargets it, and deletes
it only when the department declares it `Retired`; it never grows, shrinks or
merges one. A declared room's demand replaces `PlanStorage`'s fill-based reading
and feeds layout as before. Food declares the meal closet, the table cell, the freezer shelves
(raw meat, raw vegetables, animal corpses, the meal shelf at the dining door)
with their perishables catch-all, and the food store; Medical declares the
medicine store. All but the table cell (one cell by the built dining table) are
sited from the planned room at plan time, never from the room census. A Department that owns no store stays a grouping tag.

## Execute under supervision

Execution uses bounded native tick windows and a renewable wall-clock lease. Lease
expiry stops a controller that becomes unresponsive. Danger and player input stop
a window early:

- An injury stops it only past the native severity floor (a life-threatening stage
  or a bleed-out inside two in-game hours); a lighter wound and a discharged rest
  watch get the same medical review through a journal wake, without the
  stop-to-readmit pause. See [hazard detection bounds](hazard-detection-bounds.md).
- A routine window runs one game day (60000 ticks, the review guarantee) unless
  danger or player input stops it earlier; there is no wall-time budget. Combat
  windows stay at 300 ticks. A native work allowance (a growing field, a home
  fire) still clamps either.
- Reviews and routine orders happen at the stop between windows and under a running
  window alike: planners read one tick-consistent bundle and bind their facts to its
  tick, and the worker dispatches every routine kind live. Only the window itself is
  admitted at the stop.

The scheduler runs independently of launcher refreshes. Only one review or
execution task runs at a time. Hands yields at its operation budget and requests
continuation. Idle/blocked work and autosave refusals use a two-second retry
backoff; lease renewal and periodic observation remain independent of task
completion.

### Event delivery

The journal is the source of truth and the mod announces it (#2070): after each
appended row the mod pushes `{type: advance, newest: <cursor>}` on the
`rimgovernor.clock` GABP channel (`bridge.ClockSignal`), and the poll loop reads
the page after its own cursor with an unheld `clock_read_events`. Nothing waits in
native, so a stop is seen near-push without a held call.

- The loop takes the signal's version before each read and waits for it to move
  afterwards (at most 4 s, the `serve` bound, if an announcement is lost), so an
  announcement that lands during a read is never missed. A subscription moves the
  version too: every (re)connect ends in one tail read, and a gap shows as page
  loss exactly as before. A read that captured evidence is followed by the next at
  once. Between windows, the poll waits locally for scheduler step completion,
  then reads immediately. Without the channel the loop reads at the poll cadence.
- The poll interval remains a safety bound so a blocked step cannot hide player
  input or authority interruptions.
- A committed stop warms the pawn and emergency admission observations before
  waking the step. The step reuses them only at the same paused tick, identity and
  native generation, within its freshness bound and with no cache invalidation
  since the warm read; otherwise it reads them again.
- Every captured page wakes the scheduler step and the routine worker through their
  wake signals, which also reset the step backoff. A page whose events carry
  attempt outcomes names those actions so the worker reconciles them first, and
  while any named action is unreconciled the worker steps again at once.
- Authority changes observed while no epoch is running are journaled as owner-less
  `AuthorityChanged` rows so the next announcement carries them at once.

### Watched attempts and coupled orders

A window can be armed with watched attempts: the native supervisor stops it at the
tick boundary on which any of them reaches a terminal outcome
(`STOP_REASON_WATCH_LATCHED`, a benign stop like the tick budget).

- The scheduler arms them for a combat window only: the window's dispatched
  construction and haul attempts (the families whose native operation records
  observe their own terminal outcome, at most 16), so the fight's next step starts
  at the outcome tick.
- A routine window watches nothing: a completed order is not a reason to stop the
  clock. The `OperationOutcome` row the poll carries wakes the worker under the
  running window, and the step records the dispatched attempts the window does not
  watch (`unwatched` on the `clock_step` row) as evidence.
- A coupled order (`ActionDependency.Coupled`: a plan action written against what an
  earlier action in the same plan produced) stops nothing either. The prerequisite's
  `OperationOutcome` row is the wake, the step that sees the order ready plans live
  for it ahead of the planner wave's own cadence, and the CAS evidence its admission
  carries refuses an order whose read the world has left behind. The step row names
  the orders (`coupled_orders`).

### Step reasons

Each scheduler step carries the reason it ran, and the reason selects the planners:

| Reason | Planners run |
| --- | --- |
| Settled window, tick advance, or the 30 s safety net | Everything. |
| Timer at the same paused tick | None; only re-evaluates admission from the journal. |
| Wake | The planners that dispatch the latched outcomes' action kinds and the readers of any invalidated fact family. An authority change plans everything. |
| Own window running | Plans `live` (a full or wake step at once, a timer step when the safety net is due) and admits nothing. |

Planner facts are bound to the tick they observed, so admission holds with
`stale_planning` when they predate the admitted tick by more than the planning
tolerance (`bridge.PlanningTickTolerance`, the tightest fact family's: 250 ticks)
or a window has since outrun them. The scheduler's `MaxAge` bounds only the
admission reads.

Each step's flight-recorder `clock_step` row carries the window it admitted, the
reason the step acted on and, for a step a clock stop woke, the latency from the
native stop stamp to the step (`stop_latency_ms`), which `rimgovernor phases`
reports as steps by reason and stop-to-step latency.

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

Colony, load and map changes and stale in-flight snapshots still invalidate
pending work; that is ordinary concurrency safety, not a player-ownership rule.

A pause or letter pause only suspends routine Concerns and their open work until
control resumes in the same world (see the [overview](overview.md)).

- Drafts a suspended plan still holds (a completed draft with unfinished, unfailed
  work behind it, such as a combat hold plan's defenders) stay needed through the
  hold and the resume. The next order's drafted check refuses a pawn the player
  undrafted meanwhile (`not_drafted`).
- An explicit Pause undrafts nobody; with authority inactive the game's own
  auto-undraft applies again.
- The game's own pause on an informational letter (NeutralEvent, PositiveEvent,
  NegativeEvent, the classes the native supervisor never stops play for) stops the
  window but holds nothing: the next step admits again without a resume.

### Drafts

Drafts are plan-owned: there is no native draft claim. A plan drafts the pawns it
needs through the draft intent, and the census-based undraft sweep undrafts every
drafted colonist no live plan needs (an unsettled or still-held draft action, the
capturer of an open capture or arrest plan, or an open fight's roster), sparing a
pawn native is running an Arrest or Capture job for. RestoreWorkers stands while
such a stray draft waits for the sweep. `TestIdleDraftObservationGuards`
(buildingruntime) covers the candidate guards and admission; the colony snapshots
in `internal/snapshot` (`draft_idle_test.go`) replay the peaceful RestoreWorkers
and the threatened ActiveCombat review.

### Combat extensions

**Squad hunt.** A squad hunt is the `ActiveCombat` incident's hunt origin: while no
hostile stands and the food plan opens a formation `Hunt` candidate (a group of three or
more wild animals, or any animal a lone hunter must not designate, with three ranged
colonists able to form the squad; with fewer it Holds as `needs_gunners`), the review asserts the deficit with the channel's
prey as the occurrence's payload (`store.HuntPrey`). The fight runs through
`admitFight` with `CombatView.Hunt` set and the prey as its threats; the frame's
detail rows cover the open hunt census rows. A hostile in the frame, or the plan no
longer opening the hunt, ends the origin. An open hunt fight runs in combat windows
with the live prey as the window's acknowledged ids (`ClockWindowFacts.HuntPrey`),
so the combat budget backstop wakes its next decision; no hostile exists to make
the window a combat one otherwise.

**Royal permits** (`combat_permit.go`). An outmatched fight calls the permits its
colonists hold. With the royalty read known (`CombatView.Royalty`, the review's
`RoundsFacts.Royalty`), a held acting aid or strike permit that is off cooldown and
affordable (favor at least its cost) yields a `permit_call` order, once per holder
and permit per fight (`CombatMemory.Permitted`). Aid lands on the defender nearest
the squad's centre; a strike lands on the densest hostile clump no colonist stands
within the mortar safe radius of. Unread favor, cooldown or royalty facts hold the
call. The order is no `combat.orders` entry: the defense planner commits it as an
`ability` action (permit source, cell target) on its own incident method, and the
fight's stops wait while it is open. The admission stop makes no call.

**Psycasts** (`combat_cast.go`). With the royalty read known and a live hostile,
each standing colonist yields at most one `psycast_cast` order per stop: the ready
psycast of the highest family (heal, then stun, burst, defensive).

- Ready means its psyfocus cost fits the caster's psyfocus, its neural heat fits
  under the ceiling and its cooldown has run out, all from the royalty read
  (`PsycasterState`, `Psycast.CooldownRemaining`); an unread fact holds the cast.
- The read is slow, so each cast is remembered for the fight (`CombatMemory.Casts`):
  its cooldown, psyfocus cost and heat count against the caster until the fight ends.
- The read carries no effect category, so `psycastFamilies` classifies by vanilla def
  name and never casts an unlisted def.
- Targets (within reach of the caster): heal a defender under 60% health; stun and
  pawn-targeted burst the nearest hostile; area burst the densest hostile clump no
  colonist stands near (the strike rule); defensive the caster while a hostile is
  in reach.
- The order is no `combat.orders` entry: it commits as an `ability` action (psycast
  source) on the permit calls' incident method, native owning the guards.

## Auto takeover

A player's Manual edit is an ordinary deficit to Auto, never a provenance hold.
Recorded colony snapshots (`internal/snapshot`) replay the review over each staged
edit:

- An edited timetable or a restrictive saved diet reads as a work deficit
  (`EnsureWorkAssignments`).
- A removed Home cell opens `MaintainHomeCoverage`.
- Saved allowed-area restrictions are cleared.
- A suspended feed bill and standing release/slaughter flags open their upkeep
  Concerns; obsolete standing designations are cancelled through shared Hands.
- Resource and animal-feed production replace an inactive bill for the selected
  recipe using its native identity and the current bench snapshot. An active bill
  continues to suppress duplicate production; unrelated recipes remain unchanged.
- The diet planner restores missing natively eligible definitions while retaining
  ingredient filters and condition ranges; it never changes a shared diet in place
  or forces a pawn to eat.
- `takeover/draft` adopts and releases a standing player draft natively;
  `draft/intent` checks draft, same-state and undraft readbacks;
  `upkeep/home-coverage` checks connected Home restoration and save recovery.
- Destructive orders still require explicit opt-ins and native eligibility. A
  standing demolition order is neither a hold nor a ledger entry: an explicit
  removal adopts it, and a designation alone creates no plan need.

## Verify progress

Hands records receipts; completion tracking checks native postconditions. A
no-progress watchdog can hold stalled work. Observed completion can release that
hold without replacing the action or its evidence.

Stability requires sleeping capacity, shelter, food, production, storage, cooking,
temperature and other gates together. Changed conditions can invalidate stability.
Sustained coverage belongs in campaigns and
[GitHub issues](https://github.com/davidarcher/rimgovernor/issues).

The gated [Go routine components](../../../go/README.md#routine-policy-components)
persist maintained Standards, Method reservations and action dependencies. Building
methods reserve all costs atomically against the shared journal; Hands rechecks
native placement and observed predecessor completion before execution. Runtime
composition for additional methods and method selection remain tracked in G01.05. Go
Rounds persist Findings and hysteresis together; missing facts cannot settle
Standards. Manual cancels pending work independently of observation availability.

The opt-in Go routine building worker shares the selected player's direction and
native lease. Its journal verifies each Method's current Rounds, Concern, Episode and
world before dispatch; it cannot run arbitrary plans or acquire authority. Pending
player work takes priority. Routine building work can keep finite clock windows
eligible after the selected player plan settles. Uncertain effects still reconcile
after cancellation, while observed terminal building outcomes yield accounting to
fresh native stock and placement facts.

A worker step previews its building candidates in one native batch and each
candidate's first inspection takes its preview from that memo, reading the map
bounds and the emergency state in parallel; the inspection after durable
preparation always previews live.

Comfort joins that shared path through native access and use observations. Its
durable history distinguishes completed furniture from ordinary dining and
recreation use. After construction, a bounded clock allowance lets pawns use the
facilities; it expires from the original completion tick and cannot renew through
polling or transfer to a new player direction.
