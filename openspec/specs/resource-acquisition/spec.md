# Resource acquisition Specification

## Purpose

Obtain useful goods for colony goals through a common comparison of available
sources, their combined contributions, delivery times, costs and risks.

This specification formalizes the maintainer's confirmation review on 2026-11-03,
as traced in the [decision record](../../changes/redefine-food-requirements/proposal.md#decision-provenance-for-acquisition-growing-hunting-and-herds).
RG-ACQUIRE-001–002 are new identities; the scenarios are illustrative acceptance examples,
not separately approved tactics or evidence of passing behavior. The [supply evidence page](../../../docs/requirements/development-and-supply.md) and the [food evidence page](../../../docs/requirements/food-security.md) own the related
inspection, execution and proof limits; no implementation-completeness claim
follows from this specification. Acquisition compares every source in one shared,
decomposable comparison; habitat owns the placement of fields, pens and storage.

## Requirements

### Requirement: RG-ACQUIRE-001 — Compare combined contributions and shared costs

The governor SHALL compare acquisition options across colony goals, crediting all
useful outputs while charging shared work and resources once. Selection SHALL
account for existing stock, commitments, competing resources, time to usable
delivery and risk. Food demand SHALL participate in that shared comparison.

#### Scenario: A hunt serves leather demand when food is sufficient

- **WHEN** leather is needed and hunting is an available source although meat is plentiful
- **THEN** the hunt is eligible for comparison based on useful leather and any useful meat
- **AND** the hunt's shared costs are charged once rather than separately for each output

#### Scenario: Crops compete for a common production budget

- **WHEN** food, animal feed and clothing crops compete for land and labor
- **THEN** their contributions to colony goals are compared against the same available resources
- **AND** the same field capacity or worker time is not promised to multiple incompatible uses

#### Scenario: Expected loss justifies committing more than the point estimate

- **WHEN** expected loss from weather, raids, illness or abandonment makes the minimal plan likely to fall short
- **THEN** the governor may commit more capacity than the point estimate requires, with each committed resource promised to exactly one use
- **AND** the expected surplus is accounted for as reserve or trade stock rather than as uncounted slack

#### Scenario: Acquisition has not delivered

- **WHEN** work is planned or issued but its useful output has not reached the required destination
- **THEN** expected supply remains distinct from available inventory
- **AND** outstanding commitments remain accounted for without double-counting their output

#### Scenario: A chosen source's value is decomposable by demand

- **WHEN** a source is selected because it serves more than one colony demand
- **THEN** the governor can state how much of the source's credited value came from each demand
- **AND** the explanation uses the same shared comparison that selected the source rather than a separate account

### Requirement: RG-ACQUIRE-002 — Treat food trade as an optional fully costed source

The governor SHALL consider food and ingredient trades with traders the colony
can already reach, at home or during a journey undertaken for another purpose,
when their expected contribution justifies their full costs and risks.
Purchases and surplus sales SHALL account for future feeding and reserves.
The governor SHALL NOT mount a dedicated expedition to buy food.

#### Scenario: An accessible trader offers useful food

- **WHEN** a trader offers food that could improve colony supply
- **THEN** the purchase is compared with other uses of the resources and other food sources
- **AND** the trader's existence alone does not require a purchase

#### Scenario: Food bought away from home is not yet home supply

- **WHEN** a caravan already away for another purpose buys food from a trader
- **THEN** the purchase is compared using the caravan's carrying capacity and return time
- **AND** purchased food is not counted as usable home supply before it can arrive

#### Scenario: Stock appears surplus only in the immediate period

- **WHEN** a sale would remove food needed for expected consumption or justified reserves
- **THEN** those feeding consequences are weighed against what the sale obtains before choosing whether to sell
- **AND** the governor may sell into reserves when the acquired goods matter more than the reserve's expected feeding contribution
