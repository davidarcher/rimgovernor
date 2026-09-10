# The dashboard and game view

[Documentation](../README.md) · [System overview](overview.md)

The dashboard presents the controller's shared state and gives the player a way to
direct it. It is not another planner. Its goals, blockers, policies and receipts refer
to the same records used by automation and chat.

## Presentation should survive background work

The interface refreshes compact state while preserving drafts and the last good data. A
slow or failed refresh should not erase a message being composed or replace a useful
view with empty state. Raw identifiers and detailed tool evidence belong in diagnostics
so the main view can explain what is happening in colony terms.

The main navigation is Watch, Priorities, Work and Colony. Watch combines the game view
and chat. Priorities explains policy and verified gates; Work shows plans and their
progress, with activity available as a related view. These are different views of one
controller.

Colony centers on the individual colonists. Their dossiers combine native portraits,
worn gear, biographies, skills, health and mood with the current job report and
sampled job changes. Thoughts come from the game's stored memories and situational
cache; viewing a colonist does not recalculate their mind. An optional separate
follow view shows the pawn's surroundings without navigating the main camera.
Details and media refresh only on demand and retain their last readings on failure.

## Video and simulation are independent

The game may run while video is paused, or the view may remain active while the
simulation is paused. Viewer leases request native capture only while a viewer needs it.
Pausing video does not issue a game-time command.

When supported, the view receives frame-bound WebSocket video from native capture through the Python
server. The stream keeps the latest frame instead of queuing old frames. Unsupported or
stalled streaming falls back to snapshots, retaining the last good image. Headless
sessions cannot provide game images.

This means a smooth picture and a healthy simulation are different observations. Decoded
frame rate says something about delivery to the browser; it does not measure controller
throughput or prove low end-to-end input latency.

## Viewing does not grant control

Player time controls enter Manual. A separate load-scoped lease owns dashboard player
input. Taking control stops routine ownership before accepting that input; releasing
control leaves a Manual hold until an explicit automation resume. Stale or competing
viewers cannot reuse an old lease after a context change.

Camera navigation and stable-ID colonist selection use discovered native contracts and
readback. Private rendered Docker workers also accept native pointer, drag, wheel and
keyboard input after Take control. Each event names the displayed frame and current
owner. Changed views or delayed events are refused, and uncertain events are never
replayed. Desktop windows retain the stable-ID and camera controls.

Action follow is a separate presentation option for supported writes. It can show an
issued order, but it does not track all subsequent pawn labor or certify that the work
completed.

The React entry point is
[BridgeColony.tsx](../../dashboard/src/features/manager/BridgeColony.tsx). Server-side
controls are in [dashboard_controls.py](../../controller/rimbot/dashboard_controls.py).
See [interface contracts](../reference/interface-contracts.md) for lease, capture and
transport details, or [dashboard acceptance](../how-to/dashboard-acceptance.md) for
verification procedures.

## Visual second opinions

An optional reviewer can examine the player viewport and a detail crop from the same
image without taking control of the camera. Its concerns are shown on the retained
source image in the activity journal, so a later camera pan does not move a concern
onto unrelated scenery. These are historical visual suggestions. Even a confident
reviewer can miss a blueprint door; native facts still determine whether work is
needed. Exact report recall preserves what was observed, not its correctness.

See [source and framing contracts](../reference/interface-contracts.md#visual-review-sources)
and [visual evaluation](../how-to/visual-reviews.md) for the bounded comparison.
