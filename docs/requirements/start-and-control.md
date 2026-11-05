# Start and control a colony

[Collection](README.md) · [Review queue](review-queue.md)

## Intent and evidence ownership

The player needs a reliable way to start a session, hand the colony to the
governor and take control again. The governor has full autonomy while Auto is on.
The [control and continuity specification](../../openspec/specs/control-and-continuity/spec.md)
is the standing owner of RG-CONTROL-001–002 and their scenarios, confirmed by the
maintainer on 2026-11-05 in the [decision record](../../openspec/changes/archive/2026-11-05-confirm-remaining-capabilities/proposal.md#decision-provenance-and-review-boundary). This page owns
implementation inspection, executed results and proof limits, not requirements.
Its inspection history (C1–C5) is preserved as recorded.

## Requirement map

Each requirement is stated once, in the specification.

- RG-CONTROL-001: [Start the selected colony](../../openspec/specs/control-and-continuity/spec.md#requirement-rg-control-001--start-the-selected-colony)
- RG-CONTROL-002: [Hand control over predictably](../../openspec/specs/control-and-continuity/spec.md#requirement-rg-control-002--hand-control-over-predictably)

## Implementation status of the original requirements

The status below was recorded before the specification owned the requirement text and stays with the evidence it cites.

### Status of RG-CONTROL-001 (start the selected colony)

**Intent:** Documented in [launch](../players/launch.md) and
[controls](../players/controls.md). **Implementation:** Supported for inspected
launch, generation, loading and error presentation. Evidence C1, C4.

### Status of RG-CONTROL-002 (hand control over predictably)

**Intent:** Documented in [controls](../players/controls.md) and the
[authority contract](../developers/contracts/voluntary-control.md); full colony
autonomy was explicitly confirmed by the user on 2026-10-24.
**Implementation:** Supported through launcher controls, authority validation and
clock ownership. Interruption during native work still needs executed evidence.
Evidence C2–C3, C5.

## Evidence and inspection coverage

- **C1 — Load request.** [LauncherSaveLoaderTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/LauncherSaveLoaderTests.cs)
  checks the request sequence through unavailable routes, accepted loading and
  completion using a fake game service. It supplies no game-generation evidence.
- **C2 — Authority path.** [Control](../../brain/src/RimGovernor.Brain.Runtime/Control.cs)
  owns authority and invalidation. [NativeAuthorityControlTools](../../integrations/rimgovernor-native/src/Bridge/Protocol/NativeAuthorityControlTools.cs)
  validates mode, identity and generation before changing native control. The
  return path reports active or revoked authority. This sample covers control
  transfer at the boundary; C5 traces speed requests and the launcher controls.
- **C3 — Work under Auto.** Registered OnDemand
  [control/unassigned-runs-vanilla](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Control/ControlUnassignedVanilla.cs)
  grants Auto without running the brain and waits for a designated tree-cutting
  job to start. It asserts that no Hands assignment owns the pawn. This is useful
  evidence for [Q1](review-queue.md#q1--who-chooses-food-work), which concerns the
  division between explicit assignments and ordinary game work.

Stop and Close game have conflicting guide descriptions; track the existing
[#3998](https://github.com/davidarcher/rimgovernor/issues/3998) before treating them
as separate product promises. Save handling belongs to [continuity](continuity.md).
See [T6](review-queue.md#t6--control-and-save-boundaries).

### C4 — Launch, generation and failures

[LauncherApp.PlayAsync](../../tools/launcher/LauncherApp.cs) checks prepared
artifacts, reports failed or pending setup, starts the private game and invokes
SaveLoader for the selected save. Restart closes that game and repeats Play.
[NewColonyRunner.Generate](../../tools/launcher/NewColonyRunner.cs) validates the
specification, chooses a save name and reports progress/failure; cancellation
before generation can leave a reused game running, while cancellation during
generation closes it. [LauncherNewColonyRunnerTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/LauncherNewColonyRunnerTests.cs)
checks reuse, restart, busy-menu retry and early cancellation with a fake service.

Registered OnDemand [lifecycle/new_colony](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Lifecycle/NewColony.cs)
checks native phase order, requested colonists/biome/map, save bytes, repeatable
pinned generation and reload identity/digest. Invalid specifications are refused;
an injected save-path obstruction requires a native failure. This supplies a
defined native generation claim, without a result from this documentation pass.
Installation gating was read in LauncherApp.Ready; clean-machine installation
success and visual usability have no executed evidence here.

### C5 — Controls and clock

[MainForm](../../tools/launcher/MainForm.cs) binds the buttons to launcher actions;
[ObserveService.SetBotAsync](../../tools/launcher/Observe.cs) reads the current
world and posts Resume/Pause, returning refusal details to the page.
[ResumePauseClockOwnerTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/ResumePauseClockOwnerTests.cs)
exercises real control routes and journal with fake native authority/clock: Resume
enables running, Pause holds once, and a failed grant exposes the failed step.
[ClockTargetSpeedTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/ClockTargetSpeedTests.cs)
checks live speed changes and refusal of invalid values. The native
[time-controls policy](../../integrations/rimgovernor-native/src/Bridge/TimeControlsPolicy.cs)
routes requests according to authority. Full native interrupt/resume behavior
and visual button interaction remain execution-evidence limits.
