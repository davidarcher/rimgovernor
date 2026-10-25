# Continue safely across saves and interruptions

[Collection](README.md) · [Review queue](review-queue.md)

A loaded colony should resume from the world in that save. Work in progress needs
fresh observations before the governor decides how to continue it.

## RG-CONTINUITY-001 — Restore the saved colony's intent

Saving should retain the governor intent that the world itself cannot reconstruct.
Loading should restore that intent and rebuild derived plans from the loaded
world. A failed or uncertain save should be reported so the player knows whether
there is a confirmed checkpoint.

Loading an earlier save should recover its layout and family state. Switching
colonies should discard the previous world's derived work. A normal Auto/Manual
toggle in the same loaded world should preserve useful ongoing plans. A refused
save should leave the previous confirmed checkpoint intact.

**Intent:** Documented in [persistence](../developers/contracts/persistence-contracts.md)
and [save/resume](../players/save-and-resume.md).
**Implementation:** Supported for inspected checkpoint and world-rebuild wiring;
native save/load round trips and player-visible failures remain unassessed.
Evidence R1–R2.

## RG-CONTINUITY-002 — Reconcile interrupted work before continuing

After interruption, the governor should determine what happened to the previous
order and whether the need still exists. Fresh work should use current workers,
targets and materials. An uncertain result must remain unresolved until enough
evidence exists to continue safely. This prevents duplicated work and accidental
replacement of a newer assignment.

A completed cooking step can lead to delivery of its observed meal. A newer
assignment, changed load or uncertain result should block continuation based on
the old order. When the original need ends, the governor should release the work
it still owns. A valid later observation should allow a new decision.

**Intent:** Documented in [method continuation](../developers/contracts/method-continuation.md)
and [action completion](../developers/contracts/action-contracts.md).
**Implementation:** Supported for inspected activity continuation decisions and
journal guards; native restart recovery remains unassessed. Evidence R3.

## Evidence and inspection coverage

- **R1 — Checkpoint boundary.** [SaveBoundary](../../brain/src/RimGovernor.Brain.BuildingRuntime/SaveBoundary.cs)
  drains owned writers, checks expected world identity, calls the native save
  operation and requires a completed reply. An unavailable service, invalid
  request or uncertain result raises `CheckpointException`. This establishes
  the boundary's response, with UI presentation and disk persistence still to check.
- **R2 — World reconstruction.** [WorldRebuild](../../brain/src/RimGovernor.Brain.Host/GovernorState.cs)
  reads save-owned state, rebuilds journal ownership and family views, and clears
  derived state for a new colony/map/load identity. An authority-generation change
  alone does not trigger a rebuild. The
  [persistence ownership table](../developers/contracts/persistence-contracts.md)
  assigns saved intent to the native save, the launch journal to SQLite and
  derived views to memory or replaced tables.
- **R3 — Continuation guards.** [ActivityContinuationTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/ActivityContinuationTests.cs)
  checks terminal readback, stale revisions, changed load and authority, older
  ticks, uncertain ownership and release after the need ends. Its journal-backed
  cases verify admitted continuation and retained claims. These tests supply the
  native observations; they do not perform a game save or reload.

Autosave rotation, disk failure, corrupted saves and restart during a native write
remain unassessed. See [T6](review-queue.md#t6--control-and-save-boundaries).
