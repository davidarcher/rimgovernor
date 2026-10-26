# Controllable trade

[Contracts](README.md) · [Actions](action-contracts.md) · [Persistence](persistence-contracts.md)

Pure trade policy owns the reserve, maximum spend, supply-plan purchase caps and
export buyer aggregation. Purchase caps retain only opened resource units while
leaving medical, surgery and sale demand intact. A compatible buyer outranks an
unsupported generator, which outranks no trade; export cash uses the largest
reachable kind's observed ceiling. Runtime performs catalog lookups and fresh
sheet reads, then rechecks the selected reserve before native acceptance.

`common.TradeTarget` names exactly one map trader, settlement plus player
caravan, or orbital ship. Every target is scoped by the enclosing identity.
Native resolves map sellers on that map and world sellers in that loaded world;
a missing or foreign target refuses. The four existing trade steps retain one
live session, live prices, deal signatures; reserve and export policy live in selection. Settlement sessions use exactly the visiting player caravan's inventory and
negotiator. Orbital sessions order `UseCommsConsole`; its contact toil opens
the shared session without a dialog. Interrupted work reads absent and is not
reissued. Cancellation stops only the session's matching pending job. Native
acceptance rechecks caravan mass after staged transfers and refreshes prices
before checking the deal signature.
`domain.TradeParticipant` carries the same closed identity through action intent
and journal payloads; `domain.TradeTarget` continues to mean an economic stock
target. The words describe different contracts.

`Observations.ReadTradeAcquisition` issues no orders. It reads usable consoles,
reachable talking negotiators, every passing ship (including non-traders), and
requestable trader kinds from each native faction definition. Trader-kind
compatibility is derived from the existing mirrored TraderKindDef/stock-generator
catalog; this read supplies no estimated stock or prices. Settlement rows expose
native `CanTradeNow` and trader kind. Existing WorldRoute and CaravanState reads
own routes, caravan crew, mass, food, rot and inventory.


Current map/orbital traders provide their exact eligible negotiator, approach
cell and already-held priced stock. Opening is a coupled portfolio alternative:
the selected participant uses the existing `economy/caravan` Method and `Trade`
Open action. Shared claim admission reserves that pawn during the native walk or
comms job. Existing session operations drive the resulting native session.

Each current opportunity carries the native remaining departure ticks. Ships
read `PassingShip.ticksUntilDeparture`; stationary caravan and visitor lords read
the active departure transition's sampled `Trigger_TicksPassed.TicksLeft`.
Vanilla's timer reset on re-entering that toil is therefore observed directly.
Dismissed, departing, combat-paused and taking-wounded states offer no dated
opening. Missing lord/timer structure refuses the read rather than supplying an
unlimited visit. The shared schedule must finish the walk/comms opening before
departure, including the negotiator's earlier work and availability. Dispatch
checks the same bound. Each current purchase spends the silver in its actual trader's native
`ColonyThingsWillingToBuy(negotiator)` set, after the routine reserve and
shared medicine-first claims.

Held stock contributes only through a selected opening and affordable purchase
before departure. It cannot satisfy later wants merely because it appeared in
the census. A selected pre-departure purchase remains available to later wants
after the seller leaves; shifting the want clock preserves both the purchase
date and its deadline. No second inventory store or expiry clock is kept.

An optional existing FormCaravanIntent is a read-only packing calculation input:
the exact crew/cargo are selected using the same native formation calculation.
The reply contains mass, food/rot and crew-sensitive outbound/home WorldRoutes.
`can_pack` establishes packing prerequisites, not final formation admission;
the write still runs vanilla CheckForErrors and CheckForWarnings. The return
estimate uses the proposed outbound cargo; buying goods requires a fresh return
estimate after observing the away inventory. Unknown/unreachable estimates
cannot admit a safe autonomous mission.

## Required review facts

The trader census, colony wealth, unreserved packed-art count and active meal
ingredient slots are concrete review inputs. A missing trader section refuses the
observation; an empty census means no traders. Trade need, purchase price, silver
gap and recovery calculations carry concrete values through exports, surgery,
herd sales and mission planning. Missing currency definitions or market prices
refuse the calculation rather than producing a zero gap.

The review names `AwaitingFoodPlan` until the shared food planner has produced a plan;
this does not abort unrelated families or certify either trade Need as met.
Trade uses the shared food plan and its named runway. An absent plan refuses the
review; NoEaters produces no food purchase. Population capacity is AwaitingFoodPlan
until that runway exists, then Assessed with the capacity decision. A joiner offer
or letter requires an assessment only when it is otherwise actionable. Other
trade purchases remain reviewable while population capacity awaits the plan.

Trader generator compatibility distinguishes Trades, DoesNotTrade and
UnsupportedGenerator. Unsupported generators authorize no supply promise and
leave export buyer coverage incomplete. A kind without a currency generator has
a guaranteed silver lower bound of zero. Missing definitions refuse the read.
## Native request rules

Verified against installed RimWorld 1.6 Assembly-CSharp with ilspycmd:
`FactionDialogMaker.RequestTraderOption`, `RequestOrbitalTraderOption`,
`Faction.CalculateAdjustedGoodwillChange`/`GoodwillWith`,
`FactionRelation.CheckKindThresholds`, `Building_CommsConsole` and `PassingShip`.

| Request | Nominal goodwill | Cooldown ticks | Arrival bounds ticks |
|---|---:|---:|---:|
| Caravan | -15 | 240000 | 120000 |
| Orbital (Odyssey) | -30 | 900000 | 2500–5000 |

Effective goodwill cost uses the player's native adjusted change, not the nominal
number. Projected relation clamps base goodwill and the native goodwill-situation
ceiling. An existing Ally remains Ally while effective goodwill is above zero;
75 is the threshold for becoming Ally, not for preserving an alliance. Native
requests require Ally, expiry of the channel's faction request tick, requestable
kind and negotiator title. Caravan requests also require native allowed arrival
temperature (range narrowed by four degrees).

Vanilla orbital requests depart every passing ship before queueing an arrival.
Automatic requests therefore hold while **any** passing ship exists. Apply must
recheck this and the alliance/cooldown/title/console gates immediately before
the vanilla request action. A read does not evict ships. Arrival bounds describe
the possible vanilla delay; the exact sampled orbital arrival is unknown until
the native queue shows it.

## Request evidence and mission intent

Every routine native frame carries `TradeVisitFacts.Requests`, from the same
native request-prerequisite read as `ReadTradeAcquisition`, independently of
unmet demand. Eligible caravan kinds become portfolio alternatives. A root
selects one faction/kind; kinds of a faction exclude one another because they
share `lastTraderRequestTick`. The native adjusted goodwill payment must retain
the alliance, and the native cooldown, title, console and temperature gates must
be satisfied. Caravan requests charge goodwill and no silver fee; purchases use
today's silver above the routine reserve, with medicine's claim first. Goodwill
has no invented capability-days exchange rate.

Selection adds that one visit to that outcome's stock availability, with the
catalog kind's generator probabilities and native arrival delay. Unselected
requests add nothing. Stock is never a secured delivery. The one-day immediate
horizon stays unchanged; avoided later harm is booked in the existing Investment
term only through `InvestmentHorizonDays`, subtracting any benefit already in
the immediate harm term. See [stock arrival](forecast-contracts.md#stock-arrival-3868).

A selected request binds the exact native console and an eligible negotiator to
the shared schedule. A selected settlement option retains its exact native
validated crew, cargo and destination. Existing actions run when the external
task is first for each participant. Earlier shared work runs first; later work
is replanned from fresh observations after external completion. The existing
Method journal is the single execution owner; there is no acquisition proposal
cache or second request executor.

The selected outcome exports the medicine and part targets of its chosen
`StockWait`s, including raid losses, illness and existing surgical conditions.
One pure purchase rule allocates today's spendable silver once, medicine first,
across those targets. The forecast applies that authorization to availability;
current map/orbital trade applies it to the exact native sheet. A requested
caravan receives a fresh portfolio comparison when it arrives: requests do not
freeze colony needs or infer a trader identity from faction/kind. The current
snapshot's waits live only in `ColonyReview.TradeWaits` (see
[persistence](persistence-contracts.md)).

`CommsTradeRequestIntent` is closed to caravan/orbital requests and names faction,
kind, console, negotiator and expected last request tick. Its effect distinguishes
queued pawn work from a changed native request tick and observed goodwill. Hands
journals before dispatch. A queued receipt proves only work admission. A matching
post-dispatch native request tick plus native queue entry establishes application;
no tick change while the pawn's matching comms job is present establishes pending.
A lost receipt without sufficient native evidence remains unknown and must not
be retried merely to obtain a charge receipt. Arrival is reconciled separately.

The outbound mission, saved in the `family/economy/trade_missions` record (#3316), records home colony/map, settlement target, exact crew,
silver budget, demanded definitions/counts and mission phase/return intent. The
record and departure Method are committed atomically. Native inventory, trade stock/prices, goodwill, cooldown,
caravan position and routes stay native facts. Action attempts remain in the
session journal. Goods bought away count as away inventory until native delivery
at home is observed; request/scouting intent supplies no secured yield.

`domain.TradeMission` is the bounded, validated entry of
`family/economy/trade_missions`, driven by the `economy/trade-mission` Need. It adds home/settlement tiles for return/departure intent,
the exact negotiator, definition/count demands and packed cargo. Phases are
planned, departing, outbound, buying, returning, delivered and ended; return-home
intent is mandatory. Caravan IDs are discovered from exact native crew, never
invented or saved. Acquisition options may name a settlement before a caravan
exists; executable trade participants still require its observed caravan ID.

Preparation retains the shared departure policy's healthy spare worker, home
work owners, staffing and defense headroom. Urgent claims exclude crew members.
Native imposes no mass or food floor on the preview or the packing step: it
reports the dialog's mass, food and route facts, and the game's own
`CheckForErrors` (which refuses overweight non-reform caravans) is the only
packing veto. the brain's `SafeTradeMissionPack` is the mass and round-trip food/rot
gate. The read-only calculation seeds a legal dietary one-day pack plus reserved silver,
then replaces food with the round-trip pack and validates its full native mass,
food, rot and asymmetric routes. The final native calculation admits the trip;
unknown estimates hold it. It supplies no expected purchase yield.

Departure admission records the departing phase and Hands method atomically.
Exact observed assembly or player caravan reconstructs progress after a lost
receipt or load. Missing/overlapping crew evidence never resets a departing
mission to planned. A stopped player caravan on the settlement tile is available
to the shared session through vanilla `SettlementVisitedNow`; arrival does not
require an extra order. The `economy/trade-mission` Need drives the saved mission before
considering a new visit. The coupled portfolio selects exact packing options;
departure records `Departing` and its existing Hands action together. A restored
`Planned` mission uses the same existing departure action and immediately
records `Departing`.

Settlement purchases use live prices, bounded by the selected-wait authorization
saved in the existing mission demand, its silver budget and native carry
capacity. They use the same medicine-first purchase rule as forecast availability
and map/orbital sessions. Missing stock, insufficient cash or capacity buys fewer
units, never unrelated resource targets. Empty/unavailable sheets close trading
and return. Away purchases remain away inventory until observed home delivery.
Commitment precedes accept dispatch, so a reload never rebuys.

Return uses the same FormCaravan intent on exact observed crew, with a fresh
home route, mass, food and rot check after trading. Native home entry uses
`CaravanArrivalAction_Enter` / `UnloadIndividually`: inventory survives entry,
then pawn jobs unload it. Final travel is observed at one-tick windows.
Delivery requires exact crew on the home map and existing ListSupplies
pawn-inventory/carried holder rows proving authorized goods above any initial
packed amount of that definition. A missed held-cargo read stays unknown;
loose home stock, crew arrival and receipts never prove delivery. Lost crew
ends the attempt without delivery.

Mission sizing, live purchase allocation and the refreshed return-safety decision
are pure policy. Runtime supplies decoded catalog nutrition/mass and native
route/capacity facts. Purchase quantities reuse `BuildResourceDemand` against the
shared resource targets; silver and carry headroom are consumed once across the
live offer rows before the shared supply ranker chooses purchases.

## Settlement portfolio alternatives

Each normal review reads every trading settlement and previews a safe exact crew
and food/silver pack using native asymmetric outbound and home routes, independently
of supply demand. A saved active mission suppresses new alternatives. One mission
can be selected per outcome. Its goods become stock availability only after both
route legs; they are never secured home inventory.

The packing preview reads the selected negotiator's prices and the settlement's
held stock without accessing `Goods` (which can generate stock). Useful held goods
retain their live prices until native restock. A route arriving after restock uses
the settlement kind's catalog stock generators. Unvisited and completely empty
stock pools regenerate on access in vanilla; a depleted item in a nonempty pool
stays absent until restock. No review generates settlement stock or waits out a
restock. Purchases use today's silver above the reserve, claiming medicine first.

Verified with the installed RimWorld 1.7 `Settlement_TraderTracker`, `Tradeable`,
`TradeUtility` and `CaravanTicksPerMoveUtility`: stock regenerates lazily on visit
and expires after 30 days; buy prices include difficulty, the exact negotiator and
the settlement's native improvement (normally 0.02). Existing mixed-price stacks
of one definition use their highest observed unit price as an affordability bound.

Avoided later harm is credited only inside `InvestmentHorizonDays`, excluding
benefit already counted by the one-day immediate harm term. Crew absence costs
the free work those colonists could supply to this outcome's `ShortfallOf`, at
their observed speeds, for the round trip inside that same horizon. The shared
schedule reserves the crew from actual departure through the slice using native
roundtrip absence, not continuous awake work. This occupancy adds no labor price;
`LostWork` owns that cost. The safe native pack proves food and rot coverage;
the forecast does not invent caravan eating or rest timings. Immediate physiological
limits remain explicit in the needs timeline.
