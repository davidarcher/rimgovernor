## MODIFIED Requirements

### Requirement: RG-CONTINUITY-001 — Restore the saved colony's intent

Saving SHALL retain the governor intent that the world itself cannot
reconstruct. Loading SHALL restore that intent and rebuild derived plans from the
loaded world. A failed or uncertain save SHALL be reported so the player knows
whether there is a confirmed checkpoint. A save the player makes SHALL never be
blocked by the governor.

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

#### Scenario: A manual save outlives the flush window

- **WHEN** the player saves while the brain is connected and the brain's flush is not acknowledged within the bounded wait
- **THEN** the save proceeds with the last flushed intent and a warning is logged
- **AND** the next review after a load of that save rebuilds plans from the loaded world, so the stale intent is not acted on as if current
- **AND** a governed checkpoint keeps its stricter rule: it is reported only when the flush and save both completed
- **AND** this bounded best effort was decided 2026-11-08 ([#4011](https://github.com/davidarcher/rimgovernor/issues/4011))
