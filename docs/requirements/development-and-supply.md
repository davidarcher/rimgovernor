# Supply useful colony work

[Collection](README.md) · [Review queue](review-queue.md)

Materials matter because they enable needed work. The governor should connect
acquisition and production to the colony's actual shortages and commitments.

## RG-SUPPLY-001 — Acquire enough usable material

The governor should obtain materials needed for selected work, accounting for
stock already present and resources committed elsewhere. It should compare
available sources, assign feasible work and reassess after depletion or failure.
This avoids both stalled construction and unnecessary extraction.

When a needed material can be mined, the selected work should produce observed
stock. A depleted deposit should lead to a fresh source assessment. Missing yield
or route information should keep the option unresolved. Expected mining yield
should reserve planned supply while the stock target remains open until delivery
or other relevant native stock observation.

**Intent:** Documented in [material extraction](../developers/contracts/mining-contracts.md)
and [resource planning](../developers/architecture/space-and-resources.md).
**Implementation:** Supported for inspected deficit assessment, source selection,
production commitments and native extraction. Delivery across a whole supply
chain lacks a current executed result. Evidence S1–S2, S4.

## RG-SUPPLY-002 — Trade within the colony's means

The governor should choose supported trades that address useful demand while
retaining resources needed at home. Before accepting a deal, it should use current
prices, available stock and the actual offered terms. Completion should be
confirmed by the deal result and changed inventory.

A feasible buy-and-sell deal should exchange the selected amounts. An
unaffordable deal should be refused with a reason. Changed terms should require
fresh review. Outbound trading also needs enough people, provisions and carrying
capacity for the journey and the remaining colony.

**Intent:** Documented in [controllable trade](../developers/contracts/controllable-trade.md).
**Implementation:** Supported for inspected deal selection, native exchange and
mission reconciliation. End-to-end expedition success remains an evidence limit.
Evidence S3, S5. Some forecast handoffs have separately tracked gaps (S6).

## Evidence and inspection coverage

- **S1 — Demand into production.** [RoundsResourceOrders](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsResourceOrders.cs)
  converts reviewed resource targets into floors and declares production using
  observed stock, benches and craft facts under the resource need. The extraction
  contract describes source selection, pending yield and fresh-stock completion.
  RoundsResourcePlanner.StepRecordedAsync rereads stock, rejects stale context,
  checks deep drilling, then ranks deficits against ResourceSupplyAsync's sources.
  Acquisitions hold their promised yield until native stock can close the deficit.
- **S2 — Exact mining work.** Registered OnDemand
  [control/pawn-activity-mine](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Control/ControlMine.cs)
  stages two unroofed sandstone rocks and a skilled miner. It installs an exact
  assignment, then observes that both rocks disappear within 7,500 ticks. The
  fixture chooses the work. Its assertions cover clearing the rocks; material
  yield, delivery and autonomous deficit selection need separate evidence.
- **S3 — Native trade.** Registered OnDemand
  [trade/execute](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Trade/TradeExecute.cs)
  opens a staged caravan trade, selects buy/sell lines, accepts the signed deal
  and checks moved stock and silver. It also checks refusal of an unaffordable
  deal and cancellation. The fixture chooses the deal and places the negotiator.

### S4 — Material commitments, depletion and production

[RoundsResourceSupply](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsResourceSupply.cs)
connects selected sources to acquisitions; [RoundsDeepDrill](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsDeepDrill.cs)
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
a mission. [RoundsTrade](../../brain/src/RimGovernor.Brain.BuildingRuntime/RoundsTrade.cs)
opens a current sheet, stages lines and accepts its signature or cancels.
[TradeMissionDriver](../../brain/src/RimGovernor.Brain.BuildingRuntime/TradeMissionDriver.cs)
requires the crew home with the actual purchased cargo before reporting delivery.
[world-session cases](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Trade/TradeWorldSession.cs)
check settlement and orbital purchases and cancellation against native inventory.
A settlement purchase remains away from home until returned. Tests of arrival
probability estimate an opportunity; they do not guarantee a trader will arrive.

### S6 — Known forecast limits

Existing issues [#3917](https://github.com/davidarcher/rimgovernor/issues/3917),
[#3918](https://github.com/davidarcher/rimgovernor/issues/3918) and
[#3919](https://github.com/davidarcher/rimgovernor/issues/3919) track brain-started
trade stock, settlement missions and selection handoff. They qualify forecast
claims independently of the supported native deal path. The zero-stock bootstrap
in [#3641](https://github.com/davidarcher/rimgovernor/issues/3641) also crosses food,
workshops, clothing and shelter; prepared production cases do not establish it.

Remaining proof limits are autonomous extraction through delivered stock,
multi-stage production under competing demand and a completed trading expedition.
See [T8](review-queue.md#t8--connect-resource-demand-to-delivery).
