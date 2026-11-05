# Explanations Specification

## Purpose

Let the player understand what the governor is trying to achieve, what is
stopping it, why it chose what it chose and what evidence supports its view of
the colony.

This specification formalizes the maintainer's confirmation review on 2026-11-05,
as traced in the [decision record](../../changes/archive/2026-11-05-confirm-remaining-capabilities/proposal.md#decision-provenance-and-review-boundary).
RG-EXPLAIN-003 promotes the review queue's candidate Q5 in the narrow form the
maintainer chose. The scenarios are illustrative acceptance examples, not
evidence of passing behavior. The [explanations evidence page](../../../docs/requirements/explanations.md)
owns the related inspection, execution and proof limits; no
implementation-completeness claim follows from this specification.

## Requirements

### Requirement: RG-EXPLAIN-001 — Give a useful reason for waiting or refusal

When a need remains unmet, the governor SHALL expose the current cause and the
affected subject in language a player can use, and SHALL replace that
explanation when the cause changes, so a stalled colony is diagnosable.

#### Scenario: The cause changes

- **WHEN** blocked work waited on a missing ingredient and now waits on an unavailable worker
- **THEN** the explanation names the blocked work and the new cause replaces the old one

#### Scenario: An observation is missing

- **WHEN** the governor lacks an observation it needs
- **THEN** the explanation shows an unresolved condition rather than a guessed cause

#### Scenario: History is limited

- **WHEN** a new session starts, or a need recovers
- **THEN** the limits of the available history are clear, and the recovered need leaves the active blocker view

### Requirement: RG-EXPLAIN-002 — Distinguish plans from observed results

Status and diagnostics SHALL distinguish expected effects, accepted orders,
ongoing work and observed outcomes. Predictions SHALL retain their conditions and
missing inputs, so the player and maintainer can judge whether progress is real
and where a plan failed.

#### Scenario: A meal is forecast

- **WHEN** a meal is expected from planned cooking
- **THEN** it stays expected until production is observed

#### Scenario: An order succeeded but its effect is pending

- **WHEN** an order was accepted and dispatched while eating or treatment is still pending
- **THEN** status shows dispatch progress separately from the pending effect

#### Scenario: An observation is unavailable

- **WHEN** the observation a prediction needs is unavailable
- **THEN** the last prediction keeps that limit, and fresh evidence replaces it after recovery

### Requirement: RG-EXPLAIN-003 — Explain the comparison behind a choice

When the player asks why the governor is doing something, the governor SHALL
show what the chosen option was expected to contribute, which alternatives it
beat and by what, and the forecast's stated limits and missing inputs. The
explanation SHALL come from the comparison that made the choice, not from a
reconstruction. A choice owed under an override, such as immediate danger or
urgent care, SHALL say so.

#### Scenario: A hunt was chosen over a harvest

- **WHEN** the player asks why a pawn is hunting
- **THEN** the answer shows the hunt's expected contribution, the harvest it beat and the margin, and any forecast limit that qualified the comparison

#### Scenario: The choice was owed, not compared

- **WHEN** the player asks about work started in response to a fire, a raid or an urgent patient
- **THEN** the answer says the response was owed under that override and names what was abandoned for it

#### Scenario: The comparison had a missing input

- **WHEN** an alternative could not be priced because a required fact was unread
- **THEN** the answer names that alternative and the missing fact rather than presenting it as beaten
