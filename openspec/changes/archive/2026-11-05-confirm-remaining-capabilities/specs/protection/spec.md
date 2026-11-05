## Purpose

Keep everyone the colony holds out of immediate danger and keep the colony
ready for the dangers its forecast expects.

This specification formalizes the maintainer's confirmation review on 2026-11-04,
as traced in the [decision record](../../changes/archive/2026-11-05-confirm-remaining-capabilities/proposal.md#decision-provenance-and-review-boundary).
RG-PROTECT-002 was retired there; service restoration is owned by RG-HABITAT-003.
The scenarios are illustrative acceptance examples, not separately approved
tactics or evidence of passing behavior. The [protection evidence page](../../../docs/requirements/protection-and-recovery.md)
owns the related inspection, execution and proof limits; no
implementation-completeness claim follows from this specification.

## ADDED Requirements

### Requirement: RG-PROTECT-001 — Protect everyone the colony holds from immediate danger

The governor SHALL recognize supported threats from current observations and
answer them without a comparison for everyone within reach: colonists, animals,
prisoners, guests, stock and buildings. Which pawns respond, what work they
abandon, how the colony withdraws and what readiness it keeps beforehand SHALL
be priced like other work, each loss priced by the capability that owns it. An
unread threat remains unresolved; a cleared danger returns work to a fresh
assessment.

#### Scenario: A home fire starts

- **WHEN** a fire is observed in the home area
- **THEN** an eligible firefighter is released from ordinary work and the response is owed, not compared
- **AND** whether to save an outbuilding or let it burn is priced by what its owner loses

#### Scenario: A raid is forecast rather than present

- **WHEN** the forecast expects attacks the colony cannot yet meet
- **THEN** protection expresses readiness demand: weapons and armor through equipment management, defenses through habitat as physical plant, trained people through labor
- **AND** that demand is priced by the forecast risk and the harm it would prevent, against the colony's other work

#### Scenario: The colony cannot win a fight

- **WHEN** fighting is expected to lose more than it saves
- **THEN** withdrawal is compared with fighting: running and kiting until the raid leaves, sealing people in a room and building doors behind them, or abandoning stock
- **AND** each option is priced by its expected loss, with sunk effort at zero weight

#### Scenario: Harm reaches someone the colony does not own as a colonist

- **WHEN** a prisoner, guest or animal is within a threat's reach
- **THEN** they are protected like a colonist, and the cost of their loss is priced by the capability that holds them
- **AND** a loss nobody prices is absent from the comparison rather than zero

#### Scenario: Forecast harm uses the recorded fights' facts

- **WHEN** harm is forecast for unarmed people, people incapable of violence or people already downed
- **THEN** each state uses the same native facts as recorded fights and its own evidence
- **AND** sheltering intent does not establish immunity
