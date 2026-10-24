# Normal portfolio selection

[Architecture](overview.md) · [ColonyForecast](colony-forecast.md) · [Activity contract](../contracts/pawn-activities.md)

Normal Rounds generates alternatives from one native frame, compares their
coupled ColonyForecast consequences, then binds the selected graph through
the WorkAssignments Concern and existing Method journal. Hands remains the
only order writer. The path is enabled for observed food, needs, existing
construction sites, planned shelter completion, planned rooms and clothing
production. Planned rooms are compared and selected; binding skips their
allocations and the room reconcile places them (see [planned rooms](#planned-rooms)).

Supported cooking alternatives carry native learning and per-level work-rate
evidence into the shared allocator. Subsequent demanded meals can take less time
after learning, without a skill target or recipe unlock. The scheduler charges
work once and the comparator values changed consequences, never XP itself. See
[productive learning](../contracts/skill-learning.md#productive-cooking-in-normal-selection)
for qualified native mechanics, demand limits and observed continuation.

[Support providers](../contracts/support-work.md) add exact care and
demand-driven delivery to that same composition and comparison. Native room
effects and care timing stay separate from labor, with unknown effects explicit.
They do not restore a local helper selector underneath the chosen portfolio.

The comparison horizon is one game day. The order (see
[forecast value](forecast-value.md#comparator)) is:

1. **Safety floors.** A portfolio that lets a pawn's food or rest reach zero loses to one that does not;
   when every feasible portfolio does, the least zero-time wins; zero-times less than a tick apart tie
   (`UrgentZeroResolutionTicks`, [#3598](https://github.com/davidarcher/rimgovernor/issues/3598)). A portfolio whose expected mental breaks
   exceed `BreakRiskCap` per pawn-day loses likewise. A floor that cannot be checked says so
   (`break_floor_unassessed`, `death_floor_unassessed`) and does not pass or fail anything.
2. **Capability-days.** Hunger, rest, food poisoning, thermal exposure, mental breaks and
   wellbeing are priced in capability-days, all as losses; the portfolio that loses fewer wins. Two
   portfolios are compared on the terms both assess and the differences are summed, so an unassessed term never
   rewards a portfolio. A difference within `TieResolution` is a tie. Mood enters through the expected
   [break risk](../contracts/forecast-contracts.md#coupled-mood-break-risk) and wellbeing, never as raw threshold ticks.
3. **Terms with no conversion yet**, in this order: urgent first-care time, thermal relief, pending care batches,
   tend quality and room factors, maintenance missing XP, joy threshold exposure, requested development,
   unsupplied demand and first-care time.
4. **True ties** retain the feasible incumbent, then prefer less total participant labor among new plans.

Thermal relief is measured over the known stationary occupancy against the observed environment without that work.
It is compared only between portfolios with the same nonempty need-use
occupancy (pawn, location and interval). Adding an otherwise unneeded activity
cannot earn counterfactual shelter value. Unknown idle or transit exposure is
never credited; different occupancy intervals remain incomparable by this metric.
No injury probability is invented: harm stays unassessed (`harm_capability_unassessed`) until the harm model.
Clothing delivery is development,
not wearing or thermal protection. Completed construction effects count each
physical site once, including when a larger prerequisite bundle supplies it.
A standing Deconstruct or Uninstall designation worked, a Haul designation's stack
delivered, or a standing Cut wave of CutPlant designations cut or FloorOut wave of
RemoveFloor designations removed as one set step, is one unit of development
(`removal/{op}` relief, #3557, #3612, #3613, #3614, #3615), like a laid floor.
A planned site finished inside the horizon is recorded as one requested-development
unit (`RequestedDevelopment`), whichever root built it: its own site root, a room
alternative or a shelter. A planned room's per-cell native roof cells are together
that room's one roof unit, earned when every cell completes. No planned room's
unit counts as development, priced or not (#3828, `PlannedRoomCredits.PlannedUnits`:
each site of a room and each room's roof unit). A room wins a portfolio only on the
ledger: by its credit when priced (below), by the relief or mood its closure gives
otherwise. An unpriced ring that only ties the baseline on the ledger is not
selected. An unplaced shelf candidate is no room's, so it still counts. A priced room
also takes the `flooring/` and `removal/` relief of the floors and removals on its
interior or ring cells out of the count (a shared wall's leaves when either room is
priced), since its credit already counts them. Its sites are still emitted and exempt.

*Planned-room credit seam (#3796, epic #3650 P2/P3).* A role is priced exactly when
`ForecastCoupledContext.RoomCredits` (`PlannedRoomCredits.Shipped`: `Bedroom` and
`Barracks`, both `ForecastHousing.BedroomCredit`; `Lab`, `ForecastLaboratory.LaboratoryCredit`; `Throne` and
`Suite`, both `ForecastHousing.RoyalRoomCredit`; `Kitchen` and `Butchery`, `KitchenCredits`, #3802; `Storage`,
`MealCloset`, `Reserve` and `Yard`, all `ForecastStorage.StorageCredit`, #3798; `Morgue` and `Tomb`,
`ForecastCorpses.MorgueCredit` and `TombCredit`, #3806; `Worship`, `ForecastWorship.WorshipCredit`, #3808; `Pen`,
`ForecastPens.PenCredit`, #3812; `DeathrestChamber`, `ForecastDeathrest.ChamberCredit`, #3810) holds a credit for it:

- `delegate IReadOnlyList<RoomGain>? RoomCredit(ForecastRoomService room, outcome, context, horizonTicks)`
  returns the room's gains per served day (`RoomGain(Subject, Pawn, PerDay)`); null is unassessed.
- `context.Priced(room)` is the derived priced test, `PlannedRoomCredits.PricedRelief(context)`
  the relief needs a priced room takes out of the development count. Material charges,
  Selection's placed-ring roots and the Rounder's wall swaps read the same derived set.
- `PlannedRoomCredits.ServiceDays(context, finishedAt, horizon)` gives each room its service day:
  the day its one reconcile owes nothing (sites, placed ring pieces, floors and removals on its
  cells, all one job). Inside the schedule it is the last item's finish; left owing, it is the
  cumulative owed work (`Context.OwedWork`: finished work first, then started rooms, then the
  rest, each by its remaining work) over `PlannedBuilders`, never before the horizon.
  A blocked piece it owes (#3827: `Context.BlockedItems`, the round's read of the one predicate
  `PlannedRoomDone` uses, `RoundsBuildingPlanner.DefinitionBlockedOf`) delays nothing: the room
  serves from the day the rest is finished. A piece with no row for any other reason still leaves
  the room unassessed.
- Connectors (#3815, P4) have no credit: a circulation corridor and an airlock are prerequisites
  of the rooms they reach or seal (`Context.Connectors`, `PlannedRoomCredits.ConnectorsOf`: an
  attached airlock its parent, a corridor each non-connector room a layout door joins it to). A
  connector's owed work joins each served room's, so it delays that room's service day, and a
  connector of a priced room is priced with it (`context.Priced`): its floors' and removals' relief
  leaves the count and its deliveries are charged. A corridor serving several rooms counts for each,
  as a shared wall does; one serving no priced room keeps that relief in the count like any
  unpriced room.
- `PlannedRoomCredits.Credit` books each gain as one Investment row `{role}/{room}/{subject}`
  of `PerDay × RingClosure.CreditDays(InvestmentHorizonDays, serviceDay)` units. A priced room
  with no builders, an unknown owed work or a null credit is named `{role}_credit_unassessed:{room}`
  and credited nothing.

Work in the closure of a selected planned site root, of a priced room's placed
ring site (#3537) or room alternative (#3823), or of a commitment that carries
relief, is exempt from the thermal-relief gate on construction allocations; a
priced room is valued by its credit, so a temperate start still opens its ring.

Unknown dimensions are compared only when both alternatives assess them.
Unknown meal thoughts cannot veto known urgent hunger relief. Unknown routes,
input eligibility, expiry or capacity restrict the affected operation.
Future output is conditional: it enters only the scheduled physical lot
trajectory, never actual stock or the Method completion evidence.
Existing food lots use their own observed remaining lifetime; newly produced
food uses native fresh-definition lifetime at the observed destination. An
unrelated old stack cannot shorten or invalidate a fresh product's lifetime.

## Dependencies and search

Family providers enumerate native eligible operations, exact source quantities,
locations, timing bounds and action templates. The shared demand-driven
composer links consumed stock, reusable capabilities and exclusive facilities.
Movement is an explicit operation. A reusable construction-ready capability
does not create stock; source consumption and completed-site capabilities stay
distinct. Food supports partial allocations across native-certified sources of
the same ingredient definition. Heterogeneous ingredient substitution and
recipes on hypothetical benches without native bill intent remain unsupported.

The composer rejects cycles and incompatible realizations, shares prerequisites
and enumerates partial allocations. Root requirements need no invented native
goal task. Food and construction use this same mechanism. Shelter roof work
requires completed physical shell sites; each site keeps every construction
realization until shared comparison. A planned shelter's shell and roof are its
planned room's sites: the shelter root requires their finishes (one demand per
wall, door, roof and planned heater kind, met by any generated stuff), and the composer shares
those operations with the planned-room roots instead of repeating them. Multiple heat sources in one modeled room
are explicitly unassessed instead of choosing one locally.

Composition spends a shared 16,384-expansion generation budget, at most 2,048
per root, with 32 realizations per root. Provider input limits are 512 operations
and 2,048 observed supplies. At most 192 composed activities reserve room for
current needs in the overall 256-activity generation bound. Omitted work is
reported, never certified infeasible. Portfolio subset search spends a deterministic
allowance of outcome evaluations (`ForecastSearchBudget`, #3722): 8,192 by default (kept on
measured quality and cost in #3725,
[tuning](../testing/portfolio-allocations.md#default-budget-tuning-3725)), one unit per
complete plan a schedule search hands to the evaluator, none for a schedule or outcome
reused from the search's cache. A separate traversal bound of 65,536 scheduler
expansions over the same comparison keeps refused, duplicate, infeasible and unknown-heavy
branches finite when they evaluate nothing; each schedule search runs within 4,096 of it
and the traversal left, and a portfolio has at most 32 selected activities. One ledger
owns both counts for the whole comparison: the baseline is mandatory, evaluated at the full
bound whatever the allowance, counted and reported apart (`coupled_baseline_evaluations`,
`coupled_baseline_expansions`) but charged to neither; the warm start, the seed pass, the
extension, the confirmation, the schedule search's capacity-recovery retries and the
planned-unplaced reselection (#3557) all draw from the one allowance, and nothing nested
resets or extends it. A spent allowance starts nothing more: the search ends at the leader
found so far (the baseline when nothing was evaluated, never a fabricated winner), truncated,
with the spent limit's finding (`outcome_evaluations_exhausted`, `traversal_bound_exhausted`).
Both counts bound algorithmic work, never wall time. The default allowance was derived from
the four-worker fixture and the four default-budget tests
([derivation](../testing/portfolio-allocations.md#outcome-evaluation-budget-3722)); #3725
tunes it. Stable ordering makes replay deterministic;
the result is best found within bounds. Partial bundles remain expandable even
when neither constituent has isolated benefit.

An evaluated outcome is priced lazily (#3723, [lazy outcome
evaluation](forecast-value.md#search-engine)): its floors, harm and goal terms at
evaluation, the rest of its value ledger only when its comparison against the leader
reaches the ledger or a consumer needs the complete vector. The ranking is the one
comparator in its one order (`CompareConsequences`: urgent zero, death, break risk, the
ledger over the terms both sides assess, the unconverted terms, then the caller's incumbent
and labor rule), so the selection, the vectors of the baseline and the selected outcome,
the findings and the evaluation counts are those of an eager evaluation. Each rejected
subset's evidence (`ForecastPortfolioEvidence`) names why it lost: `infeasible_or_unassessed`
(no outcome; not assessed), `construction_without_supported_service_relief:<ids>`, or
`lost_to_leader:<stage>` with the comparator stage that decided it (`urgent_zero`,
`death_risk_floor`, `break_risk_cap`, `ledger`, an unconverted term such as `thermal_relief`
or `care_batches`, `incumbent`, `labor`); `ForecastPortfolioEvidence.StageOf` reads the
stage. A subset whose ledger was never priced (decided at a floor, or held by the relief gate
before any comparison) carries no consequence vector; one decided at or after the ledger
carries the complete vector. A subset skipped on a proven bound before its schedule was
searched (#3724, [proven bounds](forecast-value.md#safety-floors)) is reported as
`pruned:<bound>:<pawn>` (a subset in the seed, extension or confirm) or
`pruned_branch:<bound>:<pawn>` (a cardinality-sweep prefix, standing for every completion),
with no vector; `ForecastPortfolioEvidence.BoundOf` reads the bound (`urgent_zero_unserved`),
and `Assessed` is true as for a `lost_to_leader:urgent_zero` row, since both were decided by
the same comparison. The bound is taken against the current leader; the #3720 shortlist plugs
in at that comparison with the weakest retained finalist. Where no bound is available the
subset is evaluated, and the selection's `BoundsUnavailable` names why
(`urgent_zero_unserved:<reason>`).

Traversal is incumbent-seeded and screened. The previous review's selected ids come first (#3563): each
by exact match, else the first variant of the same root before `/composition/`, screened
like a seed, so the extension below starts from last review's leader when it still beats
the baseline. Single roots come next, only until a
quarter of the allowance left to this comparison (in evaluations or in expansions) is spent,
at least one unit of each, each evaluated at a screening bound of 64 scheduler
expansions (the depth-first search finds its first complete plans within a few
expansions per activity). Each planned room alternative's site roots (the first variant
per site) are then evaluated together as one unit (#3647): a room closes only once every
ring site of one stuff is built, so a wall or door root alone carries its labor and
material and none of a priced room's credit, and one-root steps never reach the pair.
The best portfolio found is then extended with each other
root in root order, again screened, and the extension repeats from every new leader
until no root improves it, so a set of any cardinality is built one root at a time. A
root exclusive with a leader member (another rest choice) swaps that member out, after
the plain additions are tried; each room unit is then added whole. The
final leader is confirmed at the full 4,096 bound. The full cardinality sweep (singles,
pairs, ...) then resumes at that bound, skipping every subset already evaluated at it,
but only when the allowance left can finish it (the subsets not yet evaluated, at this
comparison's mean cost per evaluated subset, in outcome evaluations and in expansions): a
sweep the budget cuts short has seen the subsets in root order up to some cardinality, a
lottery rather than a search, and in game it chose the selection in none of 43 reviews while
spending 13,000 of the then 16,384 expansions per review (#3563). When it cannot finish, the
search ends converged at the confirmed leader with the allowance unspent, still reported
truncated. A schedule search that finished within the screening bound is complete at any bound;
a truncated one is searched again at the larger bound. A budget that cannot finish
the single roots still reaches further contributions, and truncation stays explicit
(`portfolio_search_truncated`). A screened search's truncation is reported only when no
full-bound search of the same schedule follows. The `rounds_review` and `coupled_selection` rows report
the outcome evaluations and expansions spent against each limit, the baseline's share of each,
the expansions per phase reached (`search_phases`; a reselection's phases as `reselect/<phase>`)
and per phase the schedule searches and outcome evaluations (`search_profile`), the phase in
which the chosen subset first led (`selected_phase`) and whether the previous selection seeded
the search (`warm_start`) ([signals](../contracts/otel-signals.md#kinds)). A tiled replan
hands an unbuilt planned room a new id, so a previous id naming a room no longer in the plan is pointed at the
current room of the same role whose floor overlaps the remembered one before the search
(`RoundsRules.WarmStartOf`, the rooms remembered with the selection; `warm_translated` counts them, #3556). See
[the measured cost](../testing/portfolio-allocations.md#fixpoint-extension-with-screened-evaluation-3215)
and the [first extension](../testing/portfolio-allocations.md#incumbent-seeded-extension-3197).

Search skips an already visited exact scheduled prefix and omits a redundant
graph-only root when another selected root already contains its entire task
closure. Roots with direct consequences or claims retain separate evaluation.
Subsets with the same task closure and the same such roots (a retained incumbent
by id) share one schedule search: a shelter, a room alternative and a room's site
roots that complete the same sites are scheduled once. A planned site root differs
only in exempting its closure from the relief gate, so the gate is applied
to each subset over the shared schedule; a subset whose exempt closure also matches
is skipped.
An optimistic physical-lot quantity check rejects impossible bundles before
assignment search; timed consumption, expiry and reservations still use the
shared trajectory. These reductions preserve physical choices without a second
planner or a persistent forecast cache.

Physical demand, explicit alternative choices and physical effects have separate
identities. Different sources may contribute to the same hunger demand.
Different walls may combine; variants of one wall, physical operation, garment
choice or exclusive output cannot execute twice. Lot consumption and native
bill credits use the existing shared resource owner.

Needs projection constructs activity breakpoints once per pawn timeline and
uses a fixed five-value stack buffer for interval saturation/threshold cuts.
The cuts remain sorted and distinct, preserving both arithmetic order and
physiological occupancy identities. Mood projection scans the observed effects
and memories in their original order without creating per-point predicates;
only the unchanged current conditional offset is computed once per call. No
scratch state survives a forecast call or is shared across search branches.
The [allocation measurements](../testing/portfolio-allocations.md) pin the same
bounded-search choices and consequences across both generated fixtures.

## Needs and observed execution

Sleep candidates derive from native conditional-thought windows, physiological
thresholds, obligations and completion events. Native Night Owl workers supply
day/night awake-only effects; the planner has no trait timetable template.
Mood target and actual mood remain distinct. Actual response uses observed
seeker rise/fall and sleep-freeze mechanics; unclamped target preserves overflow.
Unknown thought mechanics, meal thoughts and future health are explicit
boundaries; memory renewal is modeled only for the bed thoughts at each wake
(`ForecastMemoryRenewal`, #3339). Sleep activation respects native thresholds, disturbance,
starvation and wake thresholds; occupied bed time is not automatically asleep.
Current-rate projections beyond one day remain unassessed.

Only selected tasks, dependencies, participants and input intent enter the
existing disposable Method journal. Receipts and fresh observations own progress.
Dependent food tasks rebind actual stacks after harvesting/delivery, including
multiple stacks for an exact same-resource recipe. Selected source quantities
and provenance remain inspectable; predicted output identifiers are not native
thing IDs. Failed consuming work is not replayed from predictions. Existing
ownership, revision, refusal, interruption and bounded delivery recovery apply.

Existing Method ownership remains protected during continuation; its unassessed
remaining duration is not credited as free future capacity. Disjoint new work
may be admitted alongside installed Methods through the same shared claim guards;
an active Method does not freeze unrelated participants. A participant whose next
service (sleep, recreation, a meal) departs more than `DistantServiceTicks` (600)
ahead, with no other participant waiting on it, is not held for it: the start
leaves it out and every later step boundary releases it to vanilla work (#3669), and a later
review selects the service again once it is due (#3047, #3598, #3649). Delayed
variants that run work after a recovery are the recovering colonist's own: no
colonist's work waits behind another colonist's meal, sleep or recreation (#3649). Independent
maintenance, building placement, fixed production bills, gear replacement,
hunting and legacy routine writers remain separate consumers. Hunting does not
use unqualified prediction estimates. The live local food ratio winner and
local construction/clothing activity dispatch paths are removed. Historical
family helper APIs remain for their tests and the separate helper-retirement
work; they are not an additional normal production selector.

`coupled_selection` records baseline, selected and rejected vectors (a rejected row
whose ledger was never priced has a reason and no vector; #3723), horizon,
unknowns and generation/search counts, with the subsets a proven bound skipped
(`search_pruned`, and `pruned` per phase on `rounds_review`) and the reasons a bound
was unavailable (`bounds_unavailable`; #3724). `coupled_observed` correlates the same
Method with observed task completion and native states. The OTLP log reader and
prepared `production/coupled-needs` case consume these signals.
Registered cases require nightly execution for observed gameplay evidence;
pure tests own preference, conservation and bounded-search claims.

## Planned rooms

A planned site has no action template until it is placed, so a selection of
planned sites starts no order (`coupled_selection` target `planned_unplaced`),
except a site's `Deconstruct` clearing, whose building stands: it is bound and
ordered like placed work (#3557).
When the chosen portfolio is unplaced planned sites alone, the comparison runs
again without the roots that reach a planned site, and that run's selected roots,
allocations and unselected-pawn defers join the selection (finding
`planned_unplaced_reselected`): the record places the planned sites while the
orders start the startable work, and vanilla work is the fallback only when nothing
startable is worth selecting (#3557, #3669). Each review keeps the latest known comparison's planned-room selection
in memory (`Rounder.RoomSelection`, a `ReviewMemory<PlannedSelection>`): the
rooms it assessed (a valued alternative in `Context.PlannedRooms`) and the
selected sites with their op, def, stuff and cells. A review that compared
nothing keeps the record; a room observed started drops its sites. An unstarted
room of the plan that the latest comparison did not assess (a running method held
the colonists, so it was no candidate) keeps its previous verdict and sites (#3676):
read as never assessed, it would be placed whole in the default stuff.

The selected sites are the planned sites the selected plan starts inside the
horizon (#3744): any of a site's activities (`ForecastPlannedSite.Activities`
and `Finishes`) allocated, whichever roots carried them (`PlannedSelectionOf`).
They are read from the plan, not the development count, so a room's sites are
placed though they count no development (#3651, #3828). A shelter root therefore selects its room's wall,
door, roof and heater; a room alternative selects its scope's sites; and the
equivalent root sets that share one schedule give one record, whichever the
search reached first. A site the plan does not start is not selected: it is
placed once its room has started. A ring that outlasts the day is placed from
the sites the day starts and valued by its projected closure (below).

A ring's value accrues from its closure day, including days past the one-day
horizon (#3744, epic #3738 D6; [roof and room consequences](colony-forecast.md#roof-and-room-consequences)).
A ring the schedule leaves open, open now or opened by a site the schedule
finishes, closes at its projected day: the builders work the ring work the
schedule finished, then the open rings, then the rings the schedule opens,
each by its remaining ring work, and the k-th ring closes at the cumulative
work over the builders. The bedroom credit runs from that day to tier 2's
horizon. Opening ring B while ring A is open puts B's scheduled work ahead of
A's remainder, so A's projected closure moves later by B's work over the
builders and A's credit pays for it: with one builder a storage ring (no
value until #3650) stays closed while a bedroom ring is open, and with three
builders a second bedroom opens beside an open one when its own credit
outweighs a third of its work. No tier table or cap decides how many rings are
open; the priced delay does.

A room is open while it is started and not done (#3754). Done means its
reconcile owes nothing a builder can do now (`PlannedRoomDone`): every owed
operation is a template piece whose definition is read unavailable or
unresearched, or whose stuff options, stock and map sources are read and offer
none (`PieceBlockedOf`; an unread fact blocks nothing, #3819), so a bed waiting on
ComplexFurniture does not keep its builders on the room, while an owed wall,
door, floor or roof does. Each room reconcile records its room's answer
(`Rounder.DoneRooms`, latest wins, envelope calls excluded); a started room with
no answer yet is open. The open rooms are the forecast's `OpenRooms`.

`ReconcileRoomAsync` gates an unstarted planned room on that record
(`PlannedRoomGateOf`):

- **Place**: the room is started (an unknown census counts as started), or its
  role has no ring (`Unfenced`, a training range). It reconciles as before, its ring placed in the base ring stuff its planned
  sites are priced in (the first ring stuff: the cheapest stocked, else the
  map's wood, else its rock blocks; #3677, #3747).
- **Hold**: none of its sites selected. The verdict is `AwaitingPlan`, subject
  `portfolio_selection` when a comparison assessed it and `portfolio_unassessed`
  when none did (after a restart, while reviews are urgent, or when the budget
  cut it from the candidates; #3754). Nothing is placed, not even template
  pieces, until a later comparison selects it: a ring opens only when priced.
  A room the latest comparison did not reach keeps its earlier selection (#3676). A room's credit ranks
  below urgent needs, so a hungry colony holds its rooms.
- **Selected**: the reconcile's operations narrow to the selected sites
  (`SelectedPlannedOps`: their ring, floor and roof cells, pieces at their
  anchors, and the clearing over them), and the ring is placed in the compared
  stuff (`SelectedRingStuff`). The blueprints then start the room, and the rest
  of it is placed as before.

A standing priced room (a role in `PlannedRoomCredits.Shipped`) is offered its walls swapped to
each other ring stuff (#3657): one `planned/room{id}/wallup/{stuff}` site per stuff
over the walls not already in it, costed at that stuff's material and the swap's
labor and valued by the stage the swap predicts (the bedroom credit). The record
keeps a started room's selected `WallUp` site, and the room reconcile swaps its
walls to that stuff (`SelectedWallUpgrade`); with none selected the walls stay as
built. The tier style's wall stuff no longer swaps a room's walls.

A room is started when a colony-built wall, door, fence or gate stands on a
ring cell that belongs to it alone, a blueprint or frame stands on its ring or
ground, or a template piece stands at its anchor (`PlannedRoomStarted`). Shared
ring cells and natural rock do not count. The record is derived and not saved:
after a restart unstarted rooms are held until the first comparison, and
started rooms are open until their first reconcile ([persistence](../contracts/persistence-contracts.md)). The forecast side is
[planned room activities](colony-forecast.md#planned-room-activities).

A room's closure and roof are valued through their consequences, not only as
requested development (#3339,
[roof and room consequences](colony-forecast.md#roof-and-room-consequences)): the
owner's bed thought from the room's predicted role and stage, the outdoors need,
undergrounder and weather thoughts, food deterioration under the roof, and the
x0.8 bench speed lifted by closure. These move the mood (`BreakRisk`) and food
tiers; the tier ordering is unchanged. Closing a walls-only room can therefore win
on mood alone, and an awful barracks honestly loses to sleeping outside. A bench's
closure-conditional variant (`{task}/closed/{room}/{stuff}`) waits for the ring and
works faster, so a comparison sees the closure's productive value too.
