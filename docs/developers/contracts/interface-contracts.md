# Dashboard and video contracts

[Documentation](../../README.md)

Outpost is the dashboard display name. These are its control, presentation and transport
contracts.

Outpost is the dashboard display name; runtime and package names remain RimGovernor. Watch
keeps chat beside the current game snapshots. Priorities explains actual priority
classes, selected methods, blockers and observed foothold gates; targets edit the same
controller policy. Work separates unfinished orders from optional history. Colony groups
people and field notes. Raw IDs, receipts and tool details stay behind closed diagnostic
disclosures. Mounted views preserve drafts across navigation, and background refreshes
preserve the last good data.

## Local colony discovery

`GET /api/colonies` lists running local Docker workers identified by the
`io.rimgovernor.colony=1` label or the `rimgovernor.container_worker` entrypoint. It returns
container ID, name, display mode, start time and a loopback URL for published
container port 8787. Missing ports produce a null URL. Discovery errors return
`colonies: null` and an error; an empty array means successful discovery of no workers.
The standalone `--colonies` server exposes the directory without starting a runtime.
Docker inspection is read-only and does not establish native game health.

Scenario workers expose a separate observation-only app at `/scenario`, attached to
the existing runtime on its event loop. `/api/state` returns retained public state
with `observationOnly: true`; unavailable/stopped runtimes return 503. Only the current
runtime is observed after replacement. `/api/camera?session_id=...` returns a retained
frame and rejects changed sessions with 409. No native reads or capture demand are
triggered. HTTP mutations return 403, and no player-control or WebSocket routes exist.
The normal controller dashboard keeps its existing interactive contracts.

## Time, camera and player control

The dashboard adds session-bound player time and camera endpoints. Time
controls enter Manual, invalidate pending execution, verify a native pause and release
owned drafts before requesting Normal, Fast or Superfast through the existing
supervisor. An in-flight review must finish before a play request; Pause remains
available. New direction or a load change prevents resuming. The clock wire also
admits Ultrafast (`rimgovernor serve --clock-speed Ultrafast`); the native tick
boost behind it (`--clock-test-acceleration`, `StartRequest.test_acceleration`)
is refused unless the game was launched with `-rimgovernor-test-acceleration`,
which only the acceptance profiles (headless and rendered) carry, so boosted
simulation stays outside the production gameplay surface. The same launch
argument gates the acceptance-only world patches (`AcceptanceWorld`, #272):
no autosaver tick, and, for a game the `test/quiet_world` fixture op marked,
no wild plant or wild animal tick outside the home area and no wild spawners.

Discrete camera navigation uses a separate player-only endpoint with a fixed
pan/zoom action set. Each request validates the live native contract under the
runtime writer lock, rechecks loaded-session identity before dispatch and reads
camera state back. Zoom stays within the reported normal range; extended zoom
is refused. Navigation turns off action follow and preserves clock/automation
settings. A session-bound camera-state read uses the same runtime lock and
native contract discovery without issuing navigation or changing control. Explicit
viewer/token credentials must name a live owner even after release; delayed owned
requests cannot fall through to the unowned controls. Native writes are sent once;
an uncertain result requires inspecting the view. These controls do not hold keys. The optional player-control lease gates
camera/time requests to one viewer. Handoff enters Manual and invalidates pending
orders before awaiting pause and owned-draft cleanup; native paused readback is
required before acknowledgement. A 15-second lease renews through heartbeats.
Expiry leaves a Manual hold, rejecting input until another explicit takeover.
Model/controller writes and generic Automate remain blocked until owner release;
load changes clear ownership. Browser blur, hidden tabs and unmount request release
without resuming automation. Only explicit Resume automation enables it again. Only the owning viewer can
select a current-map colonist through the stable-ID selector or clear selection.
The server discovers native schemas and checks both the live roster and a separate
selection readback. These explicit player operations remain outside model tools.

## Colonist dossiers and follow view

The Colony roster opens a stable-ID dossier with native biography, traits, skills,
health, equipment, needs, thoughts and sampled job history. `jobReport` is the
current native job driver's report; the definition name remains available as `job`.
Thoughts retain the bridge's cached, non-recalculating read semantics and expose
unavailable/stale situational readings. Job history contains at most eight observed
changes while a dossier is open, not a complete event log.

`GET /api/people?session_id=...` reads on demand under the runtime lock, rechecks
colony/load/map identity after collection and shares a two-second cache. The UI
polls about every 2.5 seconds only while visible and connected. Background failures
retain the last readings; a session change clears selection, history and media.

`GET /api/people/{pawn_id}/image?session_id=...&view=portrait|follow` validates the
installed `home/pawn_image` contract and scopes the response to the same session and
pawn. Portraits use native worn apparel plus the actual primary weapon's native icon
when equipped; they refresh every 15 seconds. The optional 640×400 follow view
renders a 16×10-cell neighborhood around the pawn about once a second. It is a
snapshot view, not a second WebRTC stream. It does not select/order the pawn, change
clock ownership or navigate the main camera. The native renderer temporarily uses
an offscreen target after map draw submission, restoring all changed camera fields
synchronously before presentation. Culling includes the pawn neighborhood only
during that draw. Requests have a four-second native deadline and do not queue.

Hidden views stop polling; failed media refreshes retain the last frame. Headless
sessions report images unavailable. Native refusals are structured responses so a
stale viewer cannot raise a game attention hold. The HTTP image cache is bounded to
128 session-scoped entries; portrait/follow reuse lasts 15/1 seconds respectively.

## Action follow

Action follow opts into the discovered native `watch` argument for supported real writes
only. Reads, dry runs and unsupported tools do not gain camera behavior. The preference
resets on load and is unavailable in headless mode. Native follow adds about 1.5 seconds
of viewing lead; leaving it off retains the fast write path. This frames orders, not
continuous pawn labor or every inspection. Video pause only stops dashboard capture
demand. The observed TPS indicator includes controller pauses and resets on load, rewind
or stale samples; it does not certify safety.

## Snapshot capture

The native capture is copied into immutable bytes before publication, so a subsequent
screenshot cannot truncate an in-flight HTTP response. A failed refresh retains the last
good frame and reports the delay. Visible game windows render independently of browser
viewer leases; headless sessions cannot supply video.

## Frame-bound transport

The dashboard leases capture (`POST /api/presentation/video-lease` with a
`source` of `screen`, `pawn` + `pawnId` or `map`, plus optional size and frame
rate; the reply echoes the resolved `source` and its `sourceId`), mints a
short-lived single-use ticket bound to that `sourceId`
(`POST /api/presentation/video-stream/ticket`, `{sourceId}`; absent means the
screen) and opens the same-origin `/api/presentation/video-stream` WebSocket.
One socket carries one source, so each dashboard tile (colony camera, a
colonist feed, the map overview) owns its own lease, ticket and socket and
stops only its own source (`leaseSeconds: 0` with `sourceId`; without it, every
source ends). Each binary message is a 34-byte
header (sequence, width, height, encoding, capture method, capture time,
readback cost; little-endian) followed by raw pixels; the client drops stale or
duplicate sequences and paints the latest frame.

The Go relay reads frames from the native shared-memory buffer whenever it runs
on the game's host: Windows uses a named mapping with a nonblocking mutex,
Linux a private `/dev/shm/RimGovernorVideo-<id>` mapping with nonblocking file
locks (`go/internal/videoshm`). The buffer name is the lease's `sourceId`, learned
from the first `ReadFrame` reply, which also supplies the pixel format the buffer
header does not carry. `ReadFrame` is then called about once a second only to
confirm the lease and source; a new lease publishes under a new name with a
restarted sequence. When the buffer cannot be opened (controller on another host,
lease already released) every frame goes through `ReadFrame`'s base64 media
envelope instead. Only `ReadFrame`-delivered frames are acknowledged;
`AcknowledgeFrame` is telemetry, not backpressure.

A lease names one source (`VideoStart.source`): the presented screen (the
default), a colonist (`pawn_id`, a second camera following the pawn at ten
cells of height) or the whole map. Each source has its own buffer, `sourceId`,
sequence and cadence (`frames_per_second`: screen 60, pawn up to 30, map up to
10; feeds default to 15 and 4), and `ReadFrame` selects a source by id. Feeds
render right after the game's own draw pass, with the player camera's culling
rect widened to cover them only on the frames they are due, and clip the
silhouette and overlay altitudes so a far player zoom never blanks the pawns
(their cached far-zoom sprites are still what the game submits). A lease for a
pawn that is not spawned on the current map, or for any source with no map
loaded, is refused as `supported: true, active: false` with an `unavailable`
detail; a running pawn feed ends when its pawn leaves the map, and every
rendered source ends when the current map changes (a load), so `ReadFrame` on
the old `sourceId` fails `UNAVAILABLE` and the dashboard tile re-leases and
follows the new id. Viewers of an identical spec share one source and hold
it independently by `viewer_id` (the dashboard sends one per tile as
`viewerId`): a stop or timeout by one viewer leaves the others' feed, and
the source ends with its last hold. Stopping without `source_id` drops the
viewer's hold on every source. The
`video/matrix` acceptance case measures the cost: on the reference machine five
pawn feeds cost no ticks (60 TPS, p95 frame 33 ms either way) and five pawn
feeds plus map plus screen held 55 TPS with a 50 ms p95 frame.

Unity captures the full framebuffer after rendering, at most 60 times per second
and up to 3840×2160. Private Xvfb workers capture their process-owned presented
window; optional `RIMGOVERNOR_VIDEO_READBACK=async` or `sync` selects GPU readback
or ReadPixels for comparison. The capture ceiling is not a delivered-fps
guarantee. Private display frame pacing uses 60 fps without virtual-display vsync
while capture is leased, then restores the previous settings. Native lease cleanup
unlinks the Linux buffer; an open reader sees no further sequence and closes
its mapping independently.

## Native gesture admission

`home/player_input` is explicit player infrastructure outside model capabilities.
Only a private Linux display and its process-owned game window admit direct input.
GABS establishes ownership; an ordered shared-memory mailbox dispatches events on the
native main thread without another game-order owner. Frame age, source, map/load,
camera matrices, window state and UI event revision guard gesture beginnings.
Matching key releases and context-menu right-button releases tolerate their own view
changes. Input is serialized, obsolete moves are coalesced, and gesture boundaries
are retained. Native eight-second expiry independently releases held input. Cleanup
cancels unfinished designations before releasing buttons; browser blur, hidden tabs,
disconnect, player direction and load changes also release input. No uncertain event
is retried.

## Viewer lifecycle

Viewer heartbeats renew an eight-second lease. Hidden/paused views close their peer, and
colony/load changes invalidate it. Native capture releases its resources after lease
expiry; headless mode never starts it. Connected streaming viewers do not request PNG
snapshots. Unsupported or stalled streams display the snapshot fallback, retaining the
last good image. Streaming does not change simulation speed, control ownership or
cinematic preferences.

## Connection ordering and recovery

Connection IDs scope explicit peer closure, and ordered viewer heartbeat revisions
prevent a delayed pause from overriding newer playback. Negotiation runs outside the
frame-sampling lock. The browser reconnects with bounded backoff after a stall or
transport failure and retains a recent presented-frame sample, even when the closed
track has already gone black. Hidden/paused views cancel retries. Session changes
discard retained video from the previous colony.

## Metrics and their limits

The video badge reports browser displayed fps and GPU encoding when active.
`/api/video/status` reports sampled/skipped frames, native renderer/readback cost,
per-viewer encoding, and bounded 128-sample capture-to-encoder and capture-to-display
median/p95. Selection-to-display samples span pointer dispatch through the paint of a
frame with a changed native selection fingerprint. They establish selection feedback
latency; they do not establish completion of pawn work. Missing samples stay unavailable.

## Chat and prepared-profile boundaries

Chat supplies structured evidence separately from the current player request. Request
budgeting may shorten evidence but never the protected current request; internal
preservation metadata is removed before inference. This prevents large controller
history from silently discarding the player's order.

A rendered prepared baseline may bypass the native mod mismatch only when the sole
missing recorded mod is the render-only HeadlessRim module. Missing gameplay mods retain
compatibility checks. This does not modify the saved game.

## Visual review sources

Optional visual reviews capture the current player viewport and a detail crop
from those same pixels. A normalized `focus` selects the crop; the default is the
central half. The full frame supplies wider context within that viewport only.
Neither review nor image consultation pans, zooms, selects or restores the camera.
Changed camera identity/position/zoom during capture rejects the image; player
camera movement after capture leaves a historical source, not a live overlay.
Load and player-direction guards still discard stale inference. There is no
periodic reviewer. Source PNGs are retained by SHA-256 under the private runtime's
`visual-sources`; reports contain camera provenance, dimensions and exact crop
bounds. Concern rectangles always use full-source normalized coordinates.
The activity journal displays numbered concerns on that exact historical image,
with confidence and native facts to verify. Missing sources never fall back to
the live camera. A load does not carry image archives across; unavailable
images remain explicitly unavailable. Visual, scout and consultation reports join
native results in the bounded review-local evidence index for exact recall after
conversation compaction. Recall is historical evidence, not renewed native truth.
Paired framing is not an accuracy guarantee. The configured local reviewer can
miss a visible blueprint door and overstate confidence; only native verification
can resolve such a concern. Reports never authorize construction or corrective orders.

## Related reading

Read [the dashboard and game view](../architecture/dashboard.md) before using this as a
protocol checklist.
