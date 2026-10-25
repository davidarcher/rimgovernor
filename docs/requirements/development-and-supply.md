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
**Implementation:** Unassessed across shortage-to-delivered-material. Production
declaration and exact mining execution were sampled separately. Evidence S1–S2.

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
**Implementation:** Supported for the inspected native deal execution; autonomous
deal choice, travel and home provisioning remain unassessed. Evidence S3.

## Evidence and inspection coverage

- **S1 — Demand into production.** [RoundsResourceOrders](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsResourceOrders.cs)
  converts reviewed resource targets into floors and declares production using
  observed stock, benches and craft facts under the resource need. The extraction
  contract describes source selection, pending yield and fresh-stock completion.
  Mining source selection and hauling were not fully traced in this pass.
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

Deep drilling, manufacturing chains, component supply, trade expeditions and
competition with essential work remain unassessed. Future capability development
can be added here as its outcome and evidence are examined.
See [T8](review-queue.md#t8--connect-resource-demand-to-delivery).
