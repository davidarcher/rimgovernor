# Colony commitments Specification

## Purpose

Take on people, prisoners, quests and colony practices only when what they
contribute beats what they oblige, and keep those obligations in view while
they last.

This specification formalizes the maintainer's confirmation review on
2026-11-08, as traced in the [decision record](../../changes/archive/2026-11-08-define-equipment-and-commitments/proposal.md#decision-provenance-and-review-boundary).
RG-COMMIT-002 lives in herd management. The scenarios are illustrative
acceptance examples, not separately approved tactics or evidence of passing
behavior. The [commitments evidence page](../../../docs/requirements/colony-commitments.md)
owns the related inspection, execution and proof limits; no
implementation-completeness claim follows from this specification.

## Requirements

### Requirement: RG-COMMIT-001 — Take on people and quests by priced contribution

The governor SHALL accept a joiner, lodger, prisoner or quest when its expected
contribution over the forecast horizon beats the obligations it brings: food,
beds, safety, worker time and its effect on everyone else's mood. Those
obligations SHALL stay counted while the commitment lasts, and changed capacity
SHALL reopen the decision. No population target or reserve ratio stands in for
the comparison. An unsupported or unread objective blocks with a specific
reason.

#### Scenario: A stranger asks to join

- **WHEN** a wanderer or refugee offers to join
- **THEN** their skills, labor, combat value and effect on the colony's mood are compared with the food, bed and safety they need
- **AND** their food counts toward demand from acceptance, before arrival

#### Scenario: A prisoner's fate

- **WHEN** the colony holds a prisoner
- **THEN** recruiting, converting, releasing, ransoming, enslaving, executing and harvesting are compared purely by their consequences, the colony's precepts entering through the mood and social effects the game attaches
- **AND** no outcome is excluded by rule and none is a default

#### Scenario: An accepted quest keeps its people

- **WHEN** a quest is accepted
- **THEN** the people and supplies it needs stay committed until it completes or fails
- **AND** a change in colony capacity reopens whether the remaining work is still worth it

### Requirement: RG-COMMIT-003 — Advance supported colony practices and abilities

When the colony has the people, facilities and resources for a practice or
ability, the governor SHALL weigh its benefit and obligations against other
work, including ideology roles and conversions, rituals, Royalty bestowing and
permits, neuroformers and Anomaly monolith interaction. A legal native action is
not a plan; a benefit counts only when observed. None of these SHALL be held as
unsupported where its consequences can be priced.

#### Scenario: A ritual is possible

- **WHEN** a ritual's participants, time and place are available
- **THEN** its expected mood and reward are compared with the work its participants would otherwise do
- **AND** its completion and its benefit are separate observations

#### Scenario: The monolith can be advanced

- **WHEN** the Anomaly monolith can be activated to a further level
- **THEN** the entities and events it is expected to unleash are compared with the research and abilities it unlocks and the colony's readiness for them
- **AND** an unread consequence leaves the option unpriced rather than held

#### Scenario: A role is assigned

- **WHEN** an ideology role is legally assigned
- **THEN** the assignment is read back on its participant before its benefit counts
- **AND** changed participants or conditions trigger a fresh review
