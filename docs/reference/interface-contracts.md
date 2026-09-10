# Dashboard and video contracts

[Documentation](../README.md)

Outpost is the dashboard display name. These are its control, presentation and transport
contracts.

Outpost is the dashboard display name; runtime and package names remain RimBot. Watch
keeps chat beside the current game snapshots. Priorities explains actual priority
classes, selected methods, blockers and observed foothold gates; targets edit the same
controller policy. Work separates unfinished orders from optional history. Colony groups
people and field notes. Raw IDs, receipts and tool details stay behind closed diagnostic
disclosures. Mounted views preserve drafts across navigation, and background refreshes
preserve the last good data.

## Time, camera and player control

`dashboard_controls.py` adds session-bound player time and camera endpoints. Time
controls enter Manual, invalidate pending execution, verify a native pause and release
owned drafts before requesting Normal, Fast or Superfast through the existing
supervisor. An in-flight review must finish before a play request; Pause remains
available. New direction or a load change prevents resuming. Ultrafast and boosted
simulation remain outside the production gameplay surface.

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

## WebRTC transport

Watch negotiates receive-only WebRTC through the protected `/api/video/offer` endpoint
when the `video` Python extra and native `home/video_stream` are present. There are no
input data channels or external STUN/TURN services. Up to four peers share one native
RGB24 buffer. Windows uses a named mapping and nonblocking mutex;
Linux uses a private `/dev/shm/RimBotVideo-<id>` mapping and nonblocking file locks.
Readers accept only that buffer namespace and exact capacity. Native lease cleanup
unlinks the Linux buffer; existing readers close their mappings independently.
Unity captures the
full framebuffer after rendering, at most 30 times per second and up to 3840×2160. This
uses synchronous ReadPixels and software encoding; the capture ceiling is not a
delivered-fps guarantee. Each consumer takes the latest frame instead of queuing
obsolete frames. Encoding runs off the asyncio thread; native lease renewal and buffer
sampling run separately from reviews.

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

The video badge reports browser decoded fps; its tooltip gives decoder drops and average
jitter-buffer delay when available. `/api/video/status` reports sampled and skipped
published frames, per-viewer frames handed to the encoder, and a bounded 128-sample
capture-to-encoder age median/p95. These are separate measurements; neither browser
jitter delay nor encoder-input age establishes capture-to-display latency. Capture timestamps precede framebuffer readback, and status also reports
the latest native readback duration. Missing browser metrics remain unavailable
rather than becoming zero.

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
the live camera. Paired checkpoint restore does not copy image archives; unavailable
images remain explicitly unavailable. Visual, scout and consultation reports join
native results in the bounded review-local evidence index for exact recall after
conversation compaction. Recall is historical evidence, not renewed native truth.
Paired framing is not an accuracy guarantee. The configured local reviewer can
miss a visible blueprint door and overstate confidence; only native verification
can resolve such a concern. Reports never authorize construction or corrective orders.

## Related reading

Read [the dashboard and game view](../explanation/dashboard.md) before using this as a
protocol checklist.
