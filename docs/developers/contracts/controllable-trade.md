# Controllable trade

[Contracts](README.md) · [Actions](action-contracts.md) · [Persistence](persistence-contracts.md)

`common.TradeTarget` names exactly one map trader, settlement plus player
caravan, or orbital ship. Every target is scoped by the enclosing identity.
Native resolves map sellers on that map and world sellers in that loaded world;
a missing or foreign target refuses. The four existing trade steps retain one
live session, live prices, deal signatures and economic floors. Settlement sessions use exactly the visiting player caravan's inventory and
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

An optional existing FormCaravanIntent is a read-only packing calculation input:
the exact crew/cargo are selected using the same native formation calculation.
The reply contains mass, food/rot and crew-sensitive outbound/home WorldRoutes.
`can_pack` establishes packing prerequisites, not final formation admission;
the write still runs vanilla CheckForErrors and CheckForWarnings. The return
estimate uses the proposed outbound cargo; buying goods requires a fresh return
estimate after observing the away inventory. Unknown/unreachable estimates
cannot admit a safe autonomous mission.

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

`CommsTradeRequestIntent` is closed to caravan/orbital requests and names faction,
kind, console, negotiator and expected last request tick. Its effect distinguishes
queued pawn work from a changed native request tick and observed goodwill. Hands
journals before dispatch. A queued receipt proves only work admission. A matching
post-dispatch native request tick plus native queue entry establishes application;
no tick change while the pawn's matching comms job is present establishes pending.
A lost receipt without sufficient native evidence remains unknown and must not
be retried merely to obtain a charge receipt. Arrival is reconciled separately.

The outbound saved Project records home colony/map, settlement target, exact crew,
silver budget, demanded definitions/counts and mission phase/return intent. The
Project must persist before departure, and departure remains inactive until return
and completion exist. Native inventory, trade stock/prices, goodwill, cooldown,
caravan position and routes stay native facts. Action attempts remain in the
session journal. Goods bought away count as away inventory until native delivery
at home is observed; request/scouting intent supplies no secured yield.

`domain.TradeMission` is the bounded, strictly decoded `Project.Record` for
`trade-mission` Projects. It adds home/settlement tiles for return/departure intent,
the exact negotiator, definition/count demands and packed cargo. Phases are
planned, departing, outbound, buying, returning, delivered and ended; return-home
intent is mandatory. Caravan IDs are discovered from exact native crew, never
invented or saved. Acquisition options may name a settlement before a caravan
exists; executable trade participants still require its observed caravan ID.

Preparation retains the shared departure policy's healthy spare worker, home
work owners, staffing and defense headroom. Urgent claims exclude crew members.
The native preview requires a day of food before returning mass/routes, so the
read-only calculation seeds a legal dietary one-day pack plus reserved silver,
then replaces food with the round-trip pack and validates its full native mass,
food, rot and asymmetric routes. The final native calculation admits the trip;
unknown estimates hold it. It supplies no expected purchase yield.

Departure admission records the departing phase and Hands method atomically.
Exact observed assembly or player caravan reconstructs progress after a lost
receipt or load. Missing/overlapping crew evidence never resets a departing
mission to planned. A stopped player caravan on the settlement tile is available
to the shared session through vanilla `SettlementVisitedNow`; arrival does not
require an extra order. The shared trade planner drives saved Projects before
considering a new request or visit. Completed home staffing/defense facts admit
exact packing options; the existing acquisition planner chooses without stock
credit. Departure runs only after the Project has been recorded.

Settlement purchases use live prices and current home demand, bounded by saved
authorization, mission silver and native carry capacity. The existing supply
ranker opens these candidates privately; away offers stay excluded from home
stock. A mission authorized to buy food holds in Buying while the shared food
review is unknown: it neither buys a partial plan nor records a return or
completion. This is an explicit food-plan refusal, not measured zero demand.
Known zero demand still closes trading and returns, as do empty/unavailable
sheets. Resource-only missions do not require a food-demand measurement.
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
