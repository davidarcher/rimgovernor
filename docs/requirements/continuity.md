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
**Implementation:** Supported for inspected checkpoint, save-state restoration,
world rebuild and autosave paths. Native cases define round-trip assertions;
current execution and forced disk-failure recovery remain evidence limits.
Evidence R1–R2, R4–R5.

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
journal guards. Save-reply replay is covered separately in R4; restart during
an arbitrary native write has no executed evidence here. Evidence R3–R4.

## Evidence and inspection coverage

- **R1 — Checkpoint boundary.** [SaveBoundary](../../brain/src/RimGovernor.Brain.Runtime/Control.SaveBoundary.cs)
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

### R4 — Native save and reload boundaries

Registered Nightly [lifecycle/checkpoint](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Lifecycle/Checkpoint.cs)
saves a paused game with matching identity/tick, refuses a running save, reports
an unexpected tick as uncertain and recovers the completed reply by request ID.
Unknown IDs are refused. [lifecycle/load](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Lifecycle/Load.cs)
writes governor-state blobs, replaces the set, saves and reloads it. It requires
the same colony with a new load token and identical retained blob, and checks
both map and visual readiness. This covers native serialization and load identity;
the fixture blob does not prove every family's reconstruction.

### R5 — Autosaves and manual-save cooperation

[FlushedLifecycle and Autosaver](../../brain/src/RimGovernor.Brain.Host/Autosaver.cs)
flush governor intent before a governed save and rotate completed saves through
five slots. Failure retains the slot for the next attempt and emits a warning.
[AutosaverTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/AutosaverTests.cs)
checks ordering, rotation and a failed flush with a fake save writer. The slot
counter restarts with the process; it is not a product promise about retention age.

The native [SaveHandshake](../../integrations/rimgovernor-native/src/Runtime/Persistence/SaveHandshake.cs)
suppresses vanilla autosaves and signals manual saves to flush state. It waits a
bounded time and then saves even without acknowledgement. Therefore a successful
manual game save alone cannot certify fresh governor intent. The persistence
contract describes flushing but omits the connected timeout; strict freshness
needs a product decision ([Q4](review-queue.md#q4--manual-save-freshness)).
The checkpoint path requires a completed reply, unlike that manual-save fallback.

Corrupted-save recovery and process death during arbitrary native writes have no
executed claim in this collection. Existing load/checkpoint cases should be reused
before extending coverage. See [T6](review-queue.md#t6--control-and-save-boundaries).
