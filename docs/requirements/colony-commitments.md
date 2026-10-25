# Sustain people, animals and accepted commitments

[Collection](README.md) · [Review queue](review-queue.md)

New people, animals and quests bring continuing obligations. The governor should
account for those obligations when deciding what the colony can support.

## RG-COMMIT-001 — Match commitments to available capacity

The governor should account for food, workers, shelter and relevant safety limits
when taking on supported people, herds and quests. It should retain those demands
while the obligation remains active. This protects the colony from accepting work
whose cost appears only after the decision.

A prospective joiner should count toward food demand before arrival. An accepted
quest should retain its required people and supplies through completion or
failure. An unsupported or unread objective should have a specific blocker.
Changed colony capacity should trigger reassessment of the work that remains.

**Intent:** Documented in [population](../developers/contracts/population-contracts.md),
[husbandry](../developers/contracts/husbandry-contracts.md) and
[quests](../developers/contracts/quests.md). **Implementation:** Unassessed across
commitment selection and fulfilment; contract conditions and native acceptance
were sampled. Evidence K1–K2.

## RG-COMMIT-002 — Carry out and observe animal care

The governor should maintain supported herd settings, containment and care, then
observe whether the intended animal work has happened. The purpose is a herd the
colony can feed and manage through changing numbers and conditions.

An eligible animal can receive a training designation; completed training needs
a later native observation. A missing allowed area should block that assignment.
An escaped or newly acquired animal should prompt a containment review. Food and
medical obligations should remain visible while unresolved.

**Intent:** Documented in [husbandry](../developers/contracts/husbandry-contracts.md).
**Implementation:** Supported for sampled settings writes and containment work
routing; completed training, herd feeding and recovery remain unassessed. Evidence K3.

## Evidence and inspection coverage

- **K1 — Capacity conditions.** The population contract requires a food reserve
  that increases with hosted population, accounts separately for guests and
  prisoners, and allows temporary sleeping places while beds are built. The quest
  contract protects essential workers and describes objective-specific costs,
  routes and completion. These conditions were read; their full decision paths
  were not inspected. Fixed targets in those contracts are implementation policy,
  pending any explicit product decision to make them lasting requirements.
- **K2 — Accepting a quest.** Registered OnDemand
  [quest/accept](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Quest/QuestAccept.cs)
  checks a real native quest acceptance and reward choice, refusals for invalid
  choices and replay behavior. It begins with a prepared offer and does not
  evaluate whether accepting it benefits the colony or completes its objectives.
- **K3 — Animal path.** [AnimalsReader](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsAnimals.cs)
  reads herd and room facts and routes containment and ward work through the
  shared building planner. Registered Smoke
  [husbandry/dispatch](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Husbandry/HusbandryDispatch.cs)
  applies animal settings and reads them back, including training, taming,
  slaughter, release, areas and following. Invalid combinations are refused.
  These immediate settings checks stop before animal jobs finish.

Population growth, custody outcomes, quest travel and fulfilment, ideology and
ritual effects remain unassessed. Their contracts remain the detailed references.
Food and health obligations link to [food](food-security.md) and
[care](colonist-wellbeing.md). See
[T10](review-queue.md#t10--carry-one-commitment-through-to-its-outcome).
