# Hunting Specification

## Purpose

Acquire useful wildlife products for colony goals through hunts whose expected
benefits justify participant requirements, danger and the work to recover output.

This specification formalizes the maintainer's confirmation review on 2026-11-03,
as traced in the [decision record](../../changes/archive/2026-11-03-redefine-food-requirements/proposal.md#decision-provenance-for-acquisition-growing-hunting-and-herds).
RG-HUNT-001–002 are new identities; the scenarios are illustrative acceptance examples,
not separately approved tactics or evidence of passing behavior. The [food evidence page](../../../docs/requirements/food-security.md) owns the related hunting
inspection, execution and proof limits; no implementation-completeness claim
follows from this specification. Acquisition compares every source in one shared,
decomposable comparison; habitat owns the placement of fields, pens and storage.

## Requirements

### Requirement: RG-HUNT-001 — Select hunts for net colony contribution

The governor SHALL compare hunting with other acquisition methods by useful
outputs, time to delivery, participant opportunity costs and expected harm to
participants and the colony as a whole. Hunting SHALL serve any colony goal
through shared acquisition; taming to slaughter enters the same comparison.
Required capability, including weapons, SHALL come through equipment management
with its cost and delay included.

#### Scenario: A useful hunt requires equipment the colony lacks

- **WHEN** a potentially useful hunt requires capability not currently available
- **THEN** the governor evaluates obtaining and assigning that capability as part of the option
- **AND** the hunt is not treated as ready before the capability is available

#### Scenario: Available hunting approaches have different consequences

- **WHEN** targets, participants or approaches differ in expected yield, danger or work
- **THEN** the governor compares their full consequences against colony needs and alternatives
- **AND** neither maximum yield nor minimum risk alone dictates the choice

#### Scenario: A target endangers the colony rather than the hunter

- **WHEN** a target could retaliate against the colony, explode near people or property, or draw a herd's response
- **THEN** that expected colony harm is charged against the hunt alongside participant danger
- **AND** a safe-for-the-hunter approach is not treated as safe when the colony bears the consequence

### Requirement: RG-HUNT-002 — Recover useful output and reassess changed hunts

The governor SHALL account for retrieval, processing and preservation needed to
turn a hunt into usable resources. It SHALL reassess continuation when targets,
routes, participant needs or danger change, comparing only the remaining work
and danger against the remaining expected output; effort already spent carries
no weight. Abandonment is justified when that comparison no longer favors the
hunt. A killed animal SHALL not establish completion of feeding or delivered supply.

#### Scenario: A kill leaves retrieval and processing outstanding

- **WHEN** prey is killed away from the place its products are needed
- **THEN** the remaining recovery and processing work stays in the acquisition plan
- **AND** food adequacy is based on when suitable food can actually become available

#### Scenario: Continuing a hunt no longer offers sufficient value

- **WHEN** a route becomes unsafe or a participant's needs change during a hunt
- **THEN** the governor reassesses continuation against the changed consequences
- **AND** can withdraw and choose another source rather than persist because the hunt already started

#### Scenario: A wounded target will die without further pursuit

- **WHEN** a target is wounded such that it will yield a corpse without further attack
- **THEN** the remaining work is retrieval and processing, and the remaining output is the full corpse
- **AND** the governor continues when that remaining value exceeds the remaining cost and danger
