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
