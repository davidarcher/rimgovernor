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
A planned site finished inside the horizon is one unit of requested development,
the lowest tier, whichever root built it: its own site root, a room alternative or a
shelter. A planned room's per-cell native roof cells are together that room's one
roof unit, earned when every cell completes. A whole planned room carries no value
of its own. A room whose role carries a priced value (`PricedRoomRoles`: a
bedroom, credited its occupant's mood, #3651, #3655) leaves this count; its sites are still
emitted and exempt. Work in the closure of a selected planned site root, or of
a commitment that carries relief, is exempt from the thermal-relief gate on
construction allocations.

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
reported, never certified infeasible. Portfolio subset search spends at most 65,536
expansions including scheduled evaluations, with at most 4,096 per schedule
search and 32 selected activities; the budget was 16,384 until the sweep below
stopped running when it cannot finish (#3563), which made the budget a ceiling the
converged search rarely reaches rather than the amount every review spends. Stable ordering makes replay deterministic;
the result is best found within bounds. Partial bundles remain expandable even
when neither constituent has isolated benefit.

Traversal is incumbent-seeded and screened. The previous review's selected ids come first (#3563): each
by exact match, else the first variant of the same root before `/composition/`, screened
like a seed, so the extension below starts from last review's leader when it still beats
the baseline. Single roots come next, only until a
quarter of the budget is spent, each evaluated at a screening bound of 64 scheduler
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
but only when the budget left can finish it (the subsets not yet evaluated, at the mean
cost of an evaluation so far): a sweep the budget cuts short has seen the subsets in root
order up to some cardinality, a lottery rather than a search, and in game it chose the
selection in none of 43 reviews while spending 13,000 of the 16,384 expansions per review
(#3563). When it cannot finish, the search ends converged at the confirmed leader with
the budget unspent, still reported truncated. A schedule search that finished within the screening bound is complete at any bound;
a truncated one is searched again at the larger bound. A budget that cannot finish
the single roots still reaches further contributions, and truncation stays explicit
(`portfolio_search_truncated`). A screened search's truncation is reported only when no
full-bound search of the same schedule follows. The `rounds_review` and `coupled_selection` rows report
the expansions per phase reached (`search_phases`), the phase in which the chosen subset first
led (`selected_phase`) and whether the previous selection seeded the search (`warm_start`). A tiled replan
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

`coupled_selection` records baseline, selected and rejected vectors, horizon,
unknowns and generation/search counts. `coupled_observed` correlates the same
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
nothing keeps the record; a room observed started drops its sites.

The selected sites are the planned sites whose finish the selected plan
completes inside the horizon (`ForecastCoupledOutcome.PlannedRooms`), whichever
roots carried them (`PlannedSelectionOf`). They are read from the completed
sites, not the development count, so a priced room's sites are placed too
(#3651). A shelter root therefore selects its room's wall,
door, roof and heater; a room alternative selects its scope's sites; and the
equivalent root sets that share one schedule give one record, whichever the
search reached first. A site the plan names but does not finish inside the
horizon is not selected: it is placed once its room has started.

`ReconcileRoomAsync` gates an unstarted planned room on that record
(`PlannedRoomGateOf`):

- **Place**: the room is started, or the latest comparison did not assess it.
  It reconciles as before.
- **Hold**: assessed, none of its sites selected. The verdict is
  `AwaitingPlan`, subject `portfolio_selection`; nothing is placed, not even template
  pieces, until a later comparison selects it. Planned sites rank as requested
  development, below urgent needs, so a hungry colony holds its rooms.
- **Selected**: the reconcile's operations narrow to the selected sites
  (`SelectedPlannedOps`: their ring, floor and roof cells, pieces at their
  anchors, and the clearing over them), and the ring is placed in the compared
  stuff (`SelectedRingStuff`). The blueprints then start the room, and the rest
  of it is placed as before.

A standing priced room (a `PricedRoomRoles` role) is offered its walls swapped to
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
after a restart unstarted assessed rooms place as before until the first
comparison ([persistence](../contracts/persistence-contracts.md)). The forecast side is
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
