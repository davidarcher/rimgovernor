# The dashboard and game view

[Documentation](../../README.md) · [System overview](overview.md)

The dashboard presents the Go controller's observed state and the explicit
player-command surface. It does not run its own planner; everything it shows
is a typed read of `serve`'s HTTP API (`GET /api/state`, `/api/plan`,
`/api/presentation/*`), and everything it submits goes through the same
`requestId`/CAS-guarded `/api/player/*` and `/api/<family>/plans` endpoints
any other client would use.

## Presentation should survive background work

The interface polls compact state while preserving the last good data. A slow
or failed refresh should not replace a useful view with an empty one — errors
are shown alongside the last successful reading, not in place of it.

The main navigation is Watch, Work, Colony and Help. Watch combines the live
game view with the explicit player-control panel (session token, building and
temporary-draft submission, control acquire/manual, clock review). Work is a
read-only feed of the active plan's actions and their progress; there is no
control here to cancel a step in place. Colony shows the current-map colonist
roster and portraits. These are different views of the same controller state.

Help renders the [player Markdown files](../../players/README.md) bundled at
build time. Update those files to change both repository and in-app guidance.
Help remains reachable even before the controller is detected — the
pre-connect screen carries its own Help link and plain-language launch
instructions, not just a spinner.

There is no multi-instance local colony directory (`--colonies`) or `/colonies`
route; see [issue #47](https://github.com/davidarcher/rimgovernor/issues/47).

Colony currently shows what `GET /api/presentation/colonists` actually
returns: colonist id, name, map and position. Full dossiers (worn gear,
biography, skills, health, mood, job history) have no backend read model yet —
this is a tracked gap (issue #50), not a UI omission.

## Video and simulation are independent

The game may run while video is paused, or the view may remain active while
the simulation is paused. A viewer lease (`POST /api/presentation/video-lease`)
requests native capture only while a viewer needs it; pausing video does not
issue a game-time command.

Live video is a lease → render-demand → short-lived ticket → binary WebSocket
sequence against the Go controller's own `internal/httpapi` video-stream
routes (`/api/presentation/video-lease`, `/api/presentation/render-demand`,
`/api/presentation/video-stream/ticket`, `/api/presentation/video-stream`).
Frames carry a strictly
increasing sequence number so the client can drop stale or duplicate frames.
There is currently no still-image fallback for the main viewport when video
is unsupported (`GameVideoGo.tsx` shows a status message instead); a
per-colonist still portrait is available separately via
`POST /api/presentation/pawn-image`.

## Viewing does not grant control

The bot is started for the observed world with `/api/player/control/resume`
and stopped with `/api/player/control/pause`; player submissions are guidance
the running bot executes under the world's root plan. There is no free-form
mouse/keyboard input relay in the Go controller — this is an intentional
architectural boundary, not a missing feature; see
[README.md](../../../go/README.md) for the read-only presentation guarantees.

## Implementation

The React entry point is
[ObservationDashboard.tsx](../../../dashboard/src/features/manager/ObservationDashboard.tsx),
mounted from [App.tsx](../../../dashboard/src/App.tsx) once `GET /api/health`
confirms the controller is up. Server-side handlers live under
[go/internal/httpapi](../../../go/internal/httpapi). See
[interface contracts](../contracts/interface-contracts.md) for lease, capture
and transport details.

## Retired: chat, notebook, player-authored projects, autopilot settings, visual review, local colony directory

A chat-driven command surface, a colonist notebook/memories feature, a
player-authored project list, savable autopilot settings, an automated
visual-review reviewer and the local colony directory described above have no
backend today (`POST /api/chat` currently returns `501`; the rest have no route
at all), so the dashboard does not show them rather than fake the data. See issues
[#46](https://github.com/davidarcher/rimgovernor/issues/46),
[#47](https://github.com/davidarcher/rimgovernor/issues/47),
[#49](https://github.com/davidarcher/rimgovernor/issues/49), and
[#50](https://github.com/davidarcher/rimgovernor/issues/50) for the status
of bringing these back on the Go controller.
