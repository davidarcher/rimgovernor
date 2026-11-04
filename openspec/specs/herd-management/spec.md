# Herd management Specification

## Purpose

Maintain animals whose expected contribution serves colony goals, with herd
numbers and feeding arrangements the colony can sustain through changing conditions.

This specification formalizes the maintainer's confirmation review on 2026-11-03,
as traced in the [decision record](../../changes/redefine-food-requirements/proposal.md#decision-provenance-for-acquisition-growing-hunting-and-herds).
RG-HERD-001–002 are new identities; the scenarios are illustrative acceptance examples,
not separately approved tactics or evidence of passing behavior. The [commitments evidence page](../../../docs/requirements/colony-commitments.md) owns the related animal-care
inspection, execution and proof limits; no implementation-completeness claim
follows from this specification. Acquisition compares every source in one shared,
decomposable comparison; habitat owns the placement of fields, pens and storage.

## Requirements

### Requirement: RG-HERD-001 — Choose herd size for net contribution to colony goals

The governor SHALL choose herd size and composition by colony goals, weighing
products and every other animal benefit against feed, space, labor, risk and
time to payoff. Herd management owns acquiring, breeding, keeping, slaughtering,
releasing and feeding animals. Non-production benefits SHALL be valued by the
capability that receives them through shared acquisition; an unpriced benefit
does not enter the comparison.

#### Scenario: A larger herd could improve colony feeding

- **WHEN** increasing a herd could provide useful future food
- **THEN** the governor compares that benefit with the added upkeep, delay and alternative uses of resources
- **AND** can choose growth when its net contribution justifies it

#### Scenario: An animal is useful beyond its products

- **WHEN** an animal hauls, fights, carries or provides companionship as well as or instead of producing goods
- **THEN** each such benefit enters the herd comparison at the value the receiving capability supplies
- **AND** the decision to keep, acquire or release the animal decomposes into those contributions plus its production

#### Scenario: A benefit has no owner yet

- **WHEN** an animal provides a benefit that no capability currently prices
- **THEN** that benefit is absent from the comparison rather than estimated by herd management or assigned a placeholder value
- **AND** the gap is visible in the decomposed decision so the receiving capability can be made to price it

#### Scenario: The colony lacks an animal the comparison favors

- **WHEN** acquiring an animal the colony does not have would improve its net contribution
- **THEN** the governor compares taming for keeping and trade purchase using their cost, delay, risk and upkeep
- **AND** the animal counts as part of the herd only once it is actually kept

#### Scenario: Herd upkeep exceeds its useful contribution

- **WHEN** retaining the herd no longer best serves colony goals
- **THEN** the governor evaluates breeding changes, slaughter or release using their consequences
- **AND** distinguishes planned production slaughter from an emergency inability to feed dependents

### Requirement: RG-HERD-002 — Choose sustainable grazing and supplied feeding

The governor SHALL choose grazing, supplied feed or a combination by animal
needs, sustainable pasture capacity, season, protection, land and labor costs,
accounting for foreseeable disruption and herd growth. Herd management SHALL
express pasture, pen and shelter demand to habitat, which places them. Routine
failure to provide feed SHALL not be met by routinely shedding dependents;
scarcity follows food-management priorities.

#### Scenario: Pasture and housed feeding have different costs

- **WHEN** suitable animals can graze or be sustained with grown and delivered feed
- **THEN** the governor compares the strategies using their total upkeep, risks and contribution to colony goals
- **AND** animal feeding demand participates in the shared land and crop allocation

#### Scenario: Feed and human food compete for land under an uncertain forecast

- **WHEN** feed crops and human food crops compete for the same land and the supply forecast is uncertain
- **THEN** both enter the shared comparison on equal footing, with no standing planting priority for either
- **AND** the governor hedges the human-food side harder than the feed side because the consequence of a human shortfall is worse, following the eating-time scarcity priorities rather than a separate planting rule

#### Scenario: Grazing will not sustain the herd through a seasonal gap

- **WHEN** expected grazing becomes insufficient before the next dependable supply
- **THEN** the governor prepares feed or another justified herd strategy before the gap
- **AND** food planning includes each retained animal's usable feeding access
