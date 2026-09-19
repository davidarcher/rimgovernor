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

The main navigation is Watch, Work, Colony, Governor and Help. Watch combines
the live game view with the explicit player-control panel (session token,
building and temporary-draft submission, control acquire/manual, clock
review). Work is a read-only feed of the active plan's actions and their
progress; there is no control here to cancel a step in place. Colony shows
the current-map colonist roster and portraits. Governor is the developer's
view of what the controller is doing and whether it is healthy (below).
These are different views of the same controller state.

Help renders the [player Markdown files](../../players/README.md) bundled at
build time. Update those files to change both repository and in-app guidance.
Help remains reachable even before the controller is detected — the
pre-connect screen carries its own Help link and plain-language launch
instructions, not just a spinner.

There is no multi-instance local colony directory (`--colonies`) or `/colonies`
route; see [issue #47](https://github.com/davidarcher/rimgovernor/issues/47).

Colony shows `GET /api/presentation/colonists`: each spawned free colonist's
id, name, map and position plus a dossier, the observation `PawnState` the
roster requests with `include_dossier` (needs, health and visible hediffs,
worn gear and weapons, biography with skills and traits, mood memories, the
current job). Settings and animal detail stay out of the roster; the bridge
rejects a dossier whose pawn differs from the reference or that carries them.
Job history is not observed anywhere; the dossier reports the current job only.
Beside the raw traits and skills the dossier shows the roster planner's view
of the same pawn from `GET /api/routines` `roster` (#448): the typed trait
effects (`policy.TraitEffects`: work speed, learning and move offsets,
sociability, the preference flags), the roles its traits forbid and its
backstory disables, and each usable skill's level, stored level and learn
factor, with the skills the last plan let decay marked. The roster section
opens the same report's coverage table (owners found and wanted, pawns
capable, per work type). Profiles join by native pawn id; a colonist the last
review did not plan for shows the raw dossier alone.

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

## Telemetry

`serve` keeps a flight recorder under the profile by default
(`<profile>/flight/flight.jsonl`, an 8 x 8 MiB ring; `--flight-recorder
<path>` moves it, `--no-flight-recorder` turns it off, `--observe` has no
profile and so none), and the same listener reads it back, so a live game
that seems to be doing nothing has evidence beyond stderr:

- `GET /api/telemetry/events?since=<seq>&kind=<k,...>&limit=<n>` pages the
  retained rows by sequence (`next_since` is the value for the following
  page, `more` says one is waiting); a `recording_gap` row stands in for a
  corrupt line or a sequence the ring rotated away. The sequence continues
  across launches; each row's `run` names the launch that wrote it.
- `GET /api/telemetry/metrics` is the acceptance runner's metrics block
  (#297) computed live over the current launch's rows, beside `tick`,
  `tps`, `authority` (the live generation, or null) and `last_step_ms`.

Both are read-only and unauthenticated like `/api/state`, and answer 404
without a recorder. Neither follows the tail; both share one
`bridge.TimelineReader`, which keeps rotated segments by content identity
and decodes only the bytes appended to the active file since the last
read, so a poll costs the new rows rather than the retained ring (#375;
`go/internal/httpapi/telemetry.go`). See [measure
throughput](../testing/measure-throughput.md) for what the rows carry.

The Governor view (`dashboard/src/features/governor`, #300) is the
read-only panel over both routes:

- a health strip from `/api/telemetry/metrics` polled every 2 s — tick and
  TPS, last step latency, native errors over calls, reads per step and the
  authority generation, each with a sparkline over the session's samples;
- an event feed from `/api/telemetry/events`, newest first: the first read
  probes the ring's newest sequence and pages from a bounded backfill
  behind it, then follows by `since`; rows are filtered by kind (decode
  rows hidden by default) and by a substring over the row's context and
  payload (a goal, plan, action or trace id);
- a trace view: picking any row's trace renders the rows sharing its
  `trace_id` (#298) as a waterfall, the browser twin of `rimgovernor
  trace` — each native call one line with its gate wait, the companion's
  queue/execute split and decoding, worker dispatches nested under the
  step by `parent_id`; the pick is named in the hash
  (`#governor/<trace_id>`) so a link reloads or shares it.

A serve without a recorder (404) shows why instead of the panel. The
offline [case timeline](../testing/measure-throughput.md#case-timeline) reads the same
row kinds from a case output directory; the Governor view reads a live
serve. `RIMGOVERNOR_API` points the Vite dev proxy at another serve, such
as an acceptance run's `--listen 127.0.0.1:0` port.

## Chat

`PlayerControls` shows an adviser chat panel when `POST /api/chat` is enabled
(`--chat-model`; the panel hides itself on the first 501). Each reply is an
explanation plus at most one applied policy nudge, rendered in a short reply
log; chat never authors orders (`playerData.ts` `ChatGuidance`).

## Retired: notebook, player-authored projects, autopilot settings, visual review, local colony directory

A colonist notebook/memories feature, a player-authored project list, savable
autopilot settings, an automated visual-review reviewer and the local colony
directory described above have no backend today, so the dashboard does not show
them rather than fake the data. See issues
[#47](https://github.com/davidarcher/rimgovernor/issues/47),
[#49](https://github.com/davidarcher/rimgovernor/issues/49), and
[#50](https://github.com/davidarcher/rimgovernor/issues/50) for the status
of bringing these back on the Go controller.
