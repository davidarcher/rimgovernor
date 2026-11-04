# Supply useful colony work

[Collection](README.md) · [Review queue](review-queue.md)

Materials matter because they enable needed work. The governor should connect
acquisition and production to the colony's actual shortages and commitments.

## Retired requirements

RG-SUPPLY-001 (acquire enough usable material) and RG-SUPPLY-002 (trade within
the colony's means) were recovered from developer contracts and never
maintainer-confirmed. On 2026-11-03 the maintainer folded their surviving intent
into the [resource acquisition specification](../../openspec/specs/resource-acquisition/spec.md):
extraction on the colony map is a source under RG-ACQUIRE-001, and trade in any
goods, including costed expeditions for forecast shortfalls, is RG-ACQUIRE-002.
The [reconciliation record](../../openspec/changes/archive/2026-11-03-reconcile-supply-and-commitments/proposal.md#decision-provenance-and-review-boundary)
holds the decisions. The retired identities are not reused. The evidence below
stands as inspected and is cited from that specification; none of it establishes
a pass against the RG-ACQUIRE requirements.

## Evidence and inspection coverage

- **S1 — Demand into production.** [ResourceOrders](../../brain/src/RimGovernor.Brain.Runtime/Review/ResourcePlanner.Orders.cs)
  converts reviewed resource targets into floors and declares production using
  observed stock, benches and craft facts under the resource need. The extraction
  contract describes source selection, pending yield and fresh-stock completion.
  ResourcePlanner.StepRecordedAsync rereads stock, rejects stale context,
  checks deep drilling, then ranks deficits against ResourceSupplyAsync's sources.
  Acquisitions hold their promised yield until native stock can close the deficit.
- **S2 — Native mining work.** Mining runs as native Mine designations under
  the colonists' Mining work settings; no exact pawn order mines. The former
  exact-order case `control/pawn-activity-mine` was deleted with the exact
  removal executor (task 5.6 of `native-led-labor-execution`), so no executed
  case currently proves mining yield, delivery or autonomous deficit selection.
- **S3 — Native trade.** Registered OnDemand
  [trade/execute](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Trade/TradeExecute.cs)
  opens a staged caravan trade, selects buy/sell lines, accepts the signed deal
  and checks moved stock and silver. It also checks refusal of an unaffordable
  deal and cancellation. The fixture chooses the deal and places the negotiator.

### S4 — Material commitments, depletion and production

[ResourceSupply](../../brain/src/RimGovernor.Brain.Runtime/Review/ColonyReview.ResourceSupply.cs)
connects selected sources to acquisitions; [DeepDrill](../../brain/src/RimGovernor.Brain.Runtime/Review/ResourcePlanner.DeepDrill.cs)
handles exhausted drills and replacement locations. Resource orders connect
manufacturing floors to available recipes and benches.
[ProductionPromisesTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/ProductionPromisesTests.cs)
checks that construction and batch demand add, native handoff is counted once,
completed iterations reduce commitments, and cancellation releases only work
whose native state permits it. Unread relevant stock refuses spending.
[ResourceMethodTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/ResourceMethodTests.cs)
checks missing benches, finite labor and stock demand.
[buried-steel cases](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Production/ProductionBuriedSteel.cs)
inspect roof safety and require designated ore removal with a replacement wall
blueprint. [deep-resource reads](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Tools/ToolsDeepResources.cs)
distinguish live and depleted deposits. These claims stop short of a completed
drill-to-delivered-component chain.

### S5 — Affordable trade and return cargo

[TradeAcquisitionTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/TradeAcquisitionTests.cs)
checks route, carrying mass, provisions, rot, reserves and deadlines when choosing
a mission. [Trade](../../brain/src/RimGovernor.Brain.Runtime/TradePlanner.cs)
opens a current sheet, stages lines and accepts its signature or cancels.
[TradeMissionDriver](../../brain/src/RimGovernor.Brain.Runtime/TradePlanner.MissionDriver.cs)
requires the crew home with the actual purchased cargo before reporting delivery.
[world-session cases](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Trade/TradeWorldSession.cs)
check settlement and orbital purchases and cancellation against native inventory.
A settlement purchase remains away from home until returned. Tests of arrival
probability estimate an opportunity; they do not guarantee a trader will arrive.

### S6 — Known forecast limits

Comms requests, current trader visits and exact native settlement packs enter
the shared portfolio and execute through its selected waits and existing journal
owners. The forecast keeps its one-day immediate horizon and values later harm
only through the investment horizon.
The zero-stock bootstrap
in [#3641](https://github.com/davidarcher/rimgovernor/issues/3641) also crosses food,
workshops, clothing and shelter; prepared production cases do not establish it.

Remaining proof limits are autonomous extraction through delivered stock,
multi-stage production under competing demand and a completed trading expedition.
See [T8](review-queue.md#t8--connect-resource-demand-to-delivery).

### S7 — Useful future supply and its conditions

The [stock-arrival contract](../developers/contracts/forecast-contracts.md#stock-arrival-3868)
connects production, current visitors, expected trader visits and governor-started
trade to shared demand. It preserves actual medicine kinds, prices, payment
eligibility, provisions, cargo and stock timing. A requested caravan's later
benefit belongs to the existing Investment term, capped at the investment horizon;
the immediate one-day horizon is unchanged. The same supply projection informs
medical work and useful facility value, avoiding separate promises of the same stock.

Native regenerated incident times preserve storyteller spacing within the existing
horizon. Future eligibility and successful entry or placement remain conditions.
The [conditional local incident process](../developers/contracts/forecast-contracts.md#conditional-local-incident-process)
accounts for local incidents sharing the storyteller clock. On space maps it
explicitly conditions forecasts on no intervening incidents on other maps,
caravans or quests; trade arrivals can therefore be overestimated.

The final full gate at `d0fe7897d` passed the integrated stock/trade and facility
policy checks. Those checks establish selection and forecast plumbing under their
supplied facts, not that a future trader arrives or an expedition returns its cargo.
The independently held paths and outcome-evidence limits in S5–S6 are not closed by these forecasts.
