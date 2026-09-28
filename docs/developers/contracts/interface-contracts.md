# Dashboard contracts

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
admits Ultrafast; the native tick
boost behind it (`--clock-test-acceleration`, `StartRequest.test_acceleration`)
is refused unless the game was launched with `-rimgovernor-test-acceleration`,
which only the acceptance profiles (headless and rendered) carry, so boosted
simulation stays outside the production gameplay surface. The same launch
argument gates the acceptance-only world patches (`AcceptanceWorld`, #272):
no autosaver tick, and, for a game the `test/quiet_world` fixture op marked,
no wild plant or wild animal tick outside the home area and no wild spawners.

An epoch may also carry a blind-tick budget (`--clock-blind-ticks`,
`StartRequest.blind_tick_budget`, #583). Blind ticks are those the controller
has not observed: since its last status or bundle read, or since the oldest
journal row it has not acknowledged with an events cursor, whichever is
older (an events poll only acknowledges; it observes nothing). Past the
budget native clamps `TickManager.TickRateMultiplier` to Normal at the next
frame and, once the controller reads again, doubles the ceiling every
100 ms back to unlimited; both transitions are journaled as `SpeedChanged`
(`regulated_ticks_per_second`, `blind_ticks`) and neither ends the epoch
or reads as an external speed change. The lease is the fault guard
(controller gone), the budget the liveness guard (controller slow). A
continuous ceiling (`max_ticks_per_second`) sits beside it, and an
accelerated epoch admits a live ceiling change through `SpeedRequest`
while its speed stays Ultrafast. `speedmatrix/plain`'s `regulated` row runs
uncapped under a 300-tick budget and must match the capped speeds' outcome.

Autonomous windows run at the player's own speed (#875): native reports
the last speed the player chose in the loaded game (`Status.player_speed`:
any speed assignment but the supervisor's own; unset after a load) and
`serve` starts each window at it, Ultrafast when none was chosen, so
there is no speed flag. A speed change inside a running window is still
an `external_speed_changed` stop. `--clock-test-acceleration` pins every
window to boosted Ultrafast instead. Acceptance sets a slower speed as the
player's choice (`rimworld/set_time_speed`, then a pause) before `serve`
starts (`na.WritePlayerSpeed`).

Player acceleration (every Ultrafast window without
`--clock-test-acceleration`, `StartRequest.pacing =
PACING_PLAYER_ACCELERATED`, #627) is the player-launch mode: Ultrafast only,
never with `--clock-test-acceleration` (which stays acceptance-only). Native
raises Ultrafast's multiplier between 15x and 150x against the frame budget
(30 ms, reported as `Epoch.frame_budget_ms`; the start request carries none): a frame whose tick work exceeds it
lowers the rate at once, one under 75% of it climbs 25% per 250 ms; the
game's forced slowdown is kept. Every tick of such an epoch runs the full
safety check, so hazard bounds stay tick-bounded however many ticks a frame
carries. The controller watches each critical planner wave and lowers the
epoch's `max_ticks_per_second` so a wave fits in half the safe horizon
(`--clock-blind-ticks`, default 2500 when unset), with hysteresis (changes
under 25% ignored, at most one fresh change per 5 s); it parks at Normal only
when a wave's evidence nears the whole horizon (stale), climbs back at most
doubling per fresh wave, and never
releases to full speed while any evidence is stale; a new window starts at
the earned ceiling. `Epoch.pacing_reason`/`paced_ticks_per_second` and
`Status.effective_ticks_per_second` feed the clock_step row and the
dashboard's Now panel. `speedmatrix/observations`' `player` row checks the
hazard gaps, the over-budget frame share (the manual-command dispatch bound)
and `speed_changes` per 6000 ticks.

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

## Colonist dossiers

The Colony roster opens a stable-ID dossier with native biography, traits, skills,
health, equipment, needs, thoughts and sampled job history. `jobReport` is the
current native job driver's report; the definition name remains available as `job`.
Thoughts retain the bridge's cached, non-recalculating read semantics and expose
unavailable/stale situational readings. Job history contains at most eight observed
changes while a dossier is open, not a complete event log.

`GET /api/people?session_id=...` reads on demand under the runtime lock, rechecks
colony/load/map identity after collection and shares a two-second cache. The UI
polls about every 2.5 seconds only while visible and connected. Background failures
retain the last readings; a session change clears selection and history.

## Action follow

Action follow opts into the discovered native `watch` argument for supported real writes
only. Reads, dry runs and unsupported tools do not gain camera behavior. The preference
resets on load and is unavailable in headless mode. Native follow adds about 1.5 seconds
of viewing lead; leaving it off retains the fast write path. This frames orders, not
continuous pawn labor or every inspection.
The observed TPS indicator includes controller pauses and resets on load, rewind
or stale samples; it does not certify safety.

## Snapshot capture

The native capture is copied into immutable bytes before publication, so a subsequent
screenshot cannot truncate an in-flight HTTP response. A failed refresh retains the last
good frame and reports the delay. Headless sessions
cannot supply screenshots.

## Native gesture admission

PlayerInput `LeaseInput`/`SendInput` is explicit player infrastructure outside model capabilities.
Only a private Linux display and its process-owned game window admit direct input.
The bridge session establishes ownership; an ordered shared-memory mailbox dispatches events on the
native main thread without another game-order owner. Frame age, source, map/load,
camera matrices, window state and UI event revision guard gesture beginnings.
Matching key releases and context-menu right-button releases tolerate their own view
changes. Input is serialized, obsolete moves are coalesced, and gesture boundaries
are retained. Native eight-second expiry independently releases held input. Cleanup
cancels unfinished designations before releasing buttons; browser blur, hidden tabs,
disconnect, player direction and load changes also release input. No uncertain event
is retried.

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
