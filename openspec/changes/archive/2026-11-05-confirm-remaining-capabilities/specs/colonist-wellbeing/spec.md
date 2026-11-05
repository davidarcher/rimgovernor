## Purpose

Treat patients who cannot wait and keep everyone the colony holds well enough
to do what the colony depends on them for.

This specification formalizes the maintainer's confirmation review on 2026-11-04,
as traced in the [decision record](../../changes/archive/2026-11-05-confirm-remaining-capabilities/proposal.md#decision-provenance-and-review-boundary).
The scenarios are illustrative acceptance examples, not separately approved
tactics or evidence of passing behavior. The [wellbeing evidence page](../../../docs/requirements/colonist-wellbeing.md)
owns the related inspection, execution and proof limits; no
implementation-completeness claim follows from this specification.

## ADDED Requirements

### Requirement: RG-HEALTH-001 — Treat urgent patients

The governor SHALL detect patients whose condition cannot wait and owe them
eligible care without a comparison, for colonists, prisoners, guests, animals and
downed enemies the colony has taken in alike. Which doctor or rescuer responds
and what work they leave SHALL be priced per pawn by skill and by what their
other work loses. Care is reconsidered when the patient or the available workers
change, and counts as done only when the treatment state is observed to change.

#### Scenario: Two patients and two doctors

- **WHEN** two patients need urgent care and two doctors are eligible
- **THEN** each doctor receives one patient, chosen by skill and by what each doctor's other work loses

#### Scenario: A doctor becomes unavailable

- **WHEN** the assigned doctor can no longer tend
- **THEN** a fresh eligible pairing is considered from current observations
- **AND** a missing health observation leaves the care unresolved

#### Scenario: A downed enemy after a fight

- **WHEN** the colony decides to take a downed enemy in
- **THEN** that decision is priced by whoever owns the outcome, such as recruitment or custody, with an unowned benefit absent
- **AND** once taken in, urgent care is owed without a comparison like any other patient

### Requirement: RG-HEALTH-002 — Sustain recovery and everyday wellbeing by consequence

The governor SHALL arrange ongoing care, medicine, rest, recreation, mood support
and suitable equipment, valuing each by the consequences it changes: mental
breaks, fights, work speed, illness, departure and death, each priced by the
capability that bears it. Observed improvement decides whether a need is met.
Risky procedures are compared by expected contribution against the chance of
losing the patient, with sunk effort at zero weight. Wellbeing expresses demand;
it owns no supply.

#### Scenario: Low mood is a priced risk, never ignored

- **WHEN** a pawn's mood forecast raises the chance of a mental break, a fight, slower work, or leaving the colony
- **THEN** recreation, a better room, food variety, a joy source or rest is compared by the expected loss it prevents against its cost
- **AND** a mood level with no priced consequence earns no spend by itself

#### Scenario: A procedure can kill the patient

- **WHEN** surgery, an amputation or a scarce-medicine treatment carries a chance of death
- **THEN** it is compared by what the pawn is expected to contribute afterwards against the chance of losing them
- **AND** an implant or bionic is an investment priced by the contribution it adds

#### Scenario: A need waits on something wellbeing does not own

- **WHEN** care needs medicine, clothing, a bed or a recreation spot the colony lacks
- **THEN** the need is expressed as demand: medicine to acquisition, clothing and protective gear to equipment management, beds and facilities to habitat
- **AND** the need stays open and names the obstacle until the service is observed available

#### Scenario: A recovering patient keeps care

- **WHEN** a patient's condition still needs treatment after the emergency has passed
- **THEN** care continues until observed improvement shows the need met
- **AND** a pawn exposed to dangerous temperatures is costed for clothing or protection by the harm it prevents
