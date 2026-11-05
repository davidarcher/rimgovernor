## Purpose

Give the player a reliable way to start a session, hand the colony to the
governor, take it back, and resume after a save or an interruption from the
world as it actually is.

This specification formalizes the maintainer's confirmation review on 2026-11-05,
as traced in the [decision record](../../changes/archive/2026-11-05-confirm-remaining-capabilities/proposal.md#decision-provenance-and-review-boundary).
Full colony autonomy under Auto was confirmed by the maintainer on 2026-10-24.
The scenarios are illustrative acceptance examples, not evidence of passing
behavior. The [start and control](../../../docs/requirements/start-and-control.md)
and [continuity](../../../docs/requirements/continuity.md) evidence pages own the
related inspection, execution and proof limits; no implementation-completeness
claim follows from this specification.

## ADDED Requirements

### Requirement: RG-CONTROL-001 — Start the selected colony

Play SHALL start a supported game session and load the selected save, with a
useful explanation when startup or loading fails, so the player reaches the
colony they selected without guessing whether the launcher is still working.
Restart SHALL repeat the selected launch settings.

#### Scenario: The game's routes are still starting

- **WHEN** a load is requested while the game's routes are starting
- **THEN** the load proceeds when they become available

#### Scenario: The save is missing or unusable

- **WHEN** the selected save cannot be loaded
- **THEN** a visible failure names the cause

### Requirement: RG-CONTROL-002 — Hand control over predictably

Resume SHALL give the governor control of colony actions and the clock. Pause
SHALL stop its colony actions and let the player run the game manually. A change
of world or control SHALL invalidate orders based on the previous state, so the
visible control mode stays consistent with who can act.

#### Scenario: An order prepared before Pause

- **WHEN** the player pauses after an order was prepared
- **THEN** the order is refused

#### Scenario: Resume after manual play

- **WHEN** the player resumes
- **THEN** the governor reads the current colony and continues useful work from a fresh assessment

### Requirement: RG-CONTINUITY-001 — Restore the saved colony's intent

Saving SHALL retain the governor intent that the world itself cannot
reconstruct. Loading SHALL restore that intent and rebuild derived plans from the
loaded world. A failed or uncertain save SHALL be reported so the player knows
whether there is a confirmed checkpoint.

#### Scenario: An earlier save is loaded

- **WHEN** the player loads an earlier save
- **THEN** its layout and family state are recovered and plans are rebuilt from that world

#### Scenario: The colony changes

- **WHEN** a different colony is loaded
- **THEN** the previous world's derived work is discarded
- **AND** a normal Auto/Manual toggle in the same loaded world preserves useful ongoing plans

#### Scenario: A save is refused

- **WHEN** a save fails or its result is uncertain
- **THEN** the previous confirmed checkpoint stays intact and the player is told

### Requirement: RG-CONTINUITY-002 — Reconcile interrupted work before continuing

After an interruption the governor SHALL determine what happened to the
previous order and whether the need still exists. Fresh work SHALL use current
workers, targets and materials. An uncertain result SHALL remain unresolved until
enough evidence exists to continue safely, so work is not duplicated and a newer
assignment is not replaced by accident.

#### Scenario: A completed step leads on

- **WHEN** a cooking step completed before the interruption and its meal is observed
- **THEN** delivery of that meal can proceed

#### Scenario: The old order no longer fits

- **WHEN** a newer assignment, a changed load or an uncertain result is found
- **THEN** continuation based on the old order is blocked until a valid later observation allows a new decision

#### Scenario: The original need has ended

- **WHEN** the need the order served no longer exists
- **THEN** the governor releases the work it still owns
