# Start and control a colony

[Collection](README.md) · [Review queue](review-queue.md)

The player needs a reliable way to start a session, hand the colony to the
governor and take control again. The governor has full autonomy while Auto is on.

## RG-CONTROL-001 — Start the selected colony

Play should start a supported game session and load the selected save, with a
useful explanation when startup or loading fails. This lets the player reach the
colony they selected without guessing whether the launcher is still working.

For example, a load requested while the game's routes are starting should proceed
when they become available. A missing or unusable save should leave a visible
failure. Restart should repeat the selected launch settings. The successful-load
example has a test with a simulated game service; the other examples need inspection.

**Intent:** Documented in [launch](../players/launch.md) and
[controls](../players/controls.md). **Implementation:** Unassessed across the full
launch path; the retry-to-load interaction was sampled. Evidence C1.

## RG-CONTROL-002 — Hand control over predictably

Resume should give the governor control of colony actions and the clock. Pause
should stop its colony actions and let the player run the game manually. A change
of world or control must invalidate orders based on the previous state. This
keeps the visible control mode consistent with who can act.

For example, after Pause, a previously prepared order must be refused. After
Resume, the governor should read the current colony and continue useful work.
The exact response to interruption during an order needs native execution evidence.

**Intent:** Documented in [controls](../players/controls.md) and the
[authority contract](../developers/contracts/voluntary-control.md); full colony
autonomy was explicitly confirmed by the user on 2026-10-24.
**Implementation:** Supported at the inspected authority boundary; full launcher
interaction and interruption recovery remain unassessed. Evidence C2–C3.

## Evidence and inspection coverage

- **C1 — Load request.** [LauncherSaveLoaderTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/LauncherSaveLoaderTests.cs)
  checks the request sequence through unavailable routes, accepted loading and
  completion using a fake game service. It supplies no game-generation evidence.
- **C2 — Authority path.** [Control](../../brain/src/RimGovernor.Brain.BuildingRuntime/Control.cs)
  owns authority and invalidation. [NativeAuthorityControlTools](../../integrations/rimgovernor-native/src/Bridge/Protocol/NativeAuthorityControlTools.cs)
  validates mode, identity and generation before changing native control. The
  return path reports active or revoked authority. This sample covers control
  transfer at the boundary; speed limits and launcher rendering were not traced.
- **C3 — Work under Auto.** Registered OnDemand
  [control/unassigned-runs-vanilla](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Control/ControlUnassignedVanilla.cs)
  grants Auto without running the brain and waits for a designated tree-cutting
  job to start. It asserts that no Hands assignment owns the pawn. This is useful
  evidence for [Q1](review-queue.md#q1--who-chooses-food-work), which concerns the
  division between explicit assignments and ordinary game work.

Stop and Close game have conflicting guide descriptions; track the existing
[#3998](https://github.com/davidarcher/rimgovernor/issues/3998) before treating them
as separate product promises. Save handling belongs to [continuity](continuity.md).
Generation, settings validation, installation failures and speed transitions
remain unassessed. See [T6](review-queue.md#t6--control-and-save-boundaries).
