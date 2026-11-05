# Combat Specification

## Purpose

Fight the threats the colony must answer so that the colony as a whole loses
as little as possible, and end each fight on decisions priced like any other.

This specification formalizes the maintainer's combat review on 2026-11-05, as
traced in the [decision record](../../changes/archive/2026-11-05-define-combat-requirements/proposal.md#decision-provenance-and-review-boundary).
RG-PROTECT-001 decides that a response is owed and owns readiness demand;
RG-HABITAT-009 owns defensive works; this specification owns the fight itself.
The scenarios are illustrative acceptance examples, not separately approved
tactics or evidence of passing behavior. The [protection evidence page](../../../docs/requirements/protection-and-recovery.md)
owns the related inspection, execution and proof limits; no
implementation-completeness claim follows from this specification.

## Requirements

### Requirement: RG-COMBAT-001 — Choose a fight plan by expected loss across the colony

Once a response is owed, the governor SHALL compare whole fight plans, each
naming who fights, where they stand, what they target and the condition on
which they fall back, by the expected loss and gain to the colony as a whole.
Threat type, geometry, equipment and the people available are inputs; no threat
type or named tactic owns a fixed response. Running until the threat leaves,
sealing people in and abandoning the map SHALL compete in the same comparison.

#### Scenario: A siege is set up outside the walls

- **WHEN** besiegers build mortars at range
- **THEN** a sortie, counter-battery fire, sheltering and waiting them out are compared by expected loss over the siege's forecast duration
- **AND** none of them is chosen because the threat is a siege

#### Scenario: A named tactic is a candidate, not a rule

- **WHEN** a door choke, a kiting line or a flanking squad is available for the geometry
- **THEN** each enters the comparison with its own expected loss and gain
- **AND** a fixed range, speed ratio or squad size does not decide between them

#### Scenario: The fallback condition is priced per fight

- **WHEN** a plan is chosen
- **THEN** it carries the condition under which the fighters fall back to the next position or withdraw
- **AND** that condition is set by what the colony can afford to lose in this fight, not a fixed fraction of fighters

#### Scenario: Fighting would lose more than withdrawing

- **WHEN** every fighting plan is expected to lose more than running, sealing people behind doors built in the fight, or leaving the map
- **THEN** the withdrawal plan is chosen and what it abandons is priced by the capability that owns it
- **AND** sunk effort in the fight so far has zero weight

### Requirement: RG-COMBAT-002 — Choose who fights by price

The governor SHALL choose fighters per pawn by skill, weapon, armor, health,
the chance of death in this fight and what their absence costs elsewhere; no
fixed health cutoff or player preference holds a pawn back. Trained attack
animals are priced participants; other animals are moved out of the fight.
Prisoners are never armed and are protected in place. Guests are neither
counted on nor ordered. Weapons and armor the plan needs are equipment demand
under RG-PROTECT-001.

#### Scenario: A wounded pawn is still the best shot

- **WHEN** a pawn below half health is the colony's best marksman behind full cover
- **THEN** they are compared on their chance of death and contribution, not excluded by a health threshold

#### Scenario: The colony is unarmed

- **WHEN** the forecast expects a threat the colony cannot arm for
- **THEN** the shortfall is expressed as equipment demand priced by the forecast risk
- **AND** the shortfall is never held as unsupported

#### Scenario: Animals and prisoners in a fight

- **WHEN** a fight starts with trained attack animals, herd animals and prisoners present
- **THEN** attack animals are priced as fighters by the herd capability's value for them, herd animals are moved out of the fight, and prisoners stay unarmed and protected

### Requirement: RG-COMBAT-003 — Carry out the plan and reconsider from observation

The governor SHALL carry out the chosen plan as one plan, with its state
readable, and SHALL reconsider it at every review from fresh observations. A
pawn whose chance of death has risen is pulled back when that costs less than
their contribution. When the fallback condition is met, the fallback is taken
without a new comparison. Violence from inside, such as an aggressive mental
break or a prison break, is answered under the protection override and the
response plan is priced like any other.

#### Scenario: A fighter is badly hurt

- **WHEN** a fighter's observed condition raises their chance of death
- **THEN** pulling them back and the rescue path are compared with keeping them in position
- **AND** a rescue exposes the rescuer by their own priced chance of harm

#### Scenario: A colonist breaks and attacks

- **WHEN** a colonist starts an aggressive mental break
- **THEN** a response is owed for everyone in reach
- **AND** subduing, isolating, arresting or waiting behind a closed door is chosen by harm to the breaker and to the responders

#### Scenario: The plan's assumption fails

- **WHEN** a fresh observation shows the geometry, the enemy or the fighters differ from what the plan assumed
- **THEN** the comparison is run again from those observations
- **AND** a missing required observation leaves the affected plan unpriced rather than assumed

### Requirement: RG-COMBAT-004 — End the fight by priced decisions

When the enemy breaks, the governor SHALL compare pursuit with letting them go
by expected loot, captives and fewer repeat raids against the exposure of pawns
away from cover. Each downed enemy SHALL be handled by comparing kill, capture,
treat and release, and leave, each priced by the capability that owns the
outcome. No trait or addiction decides the outcome. The fight ends when
observations show no hostile standing.

#### Scenario: Raiders flee

- **WHEN** the remaining raiders run for the map edge
- **THEN** pursuit is chosen when its expected gain beats the expected exposure
- **AND** otherwise the fighters are released and recovery begins

#### Scenario: A raider is downed

- **WHEN** a hostile is downed and the fight allows reaching them
- **THEN** killing, capturing, treating and releasing, and leaving them are compared by their priced consequences: recruitment and ransom and custody by commitments, the mood cost of killing by wellbeing
- **AND** an outcome nobody prices is absent from the comparison rather than a default

#### Scenario: The fight is over

- **WHEN** observations show no hostile standing and no pursuit worth making
- **THEN** every fighter is released and the work abandoned for the fight returns to a fresh assessment
- **AND** a hostile that is unread keeps the fight open
