# External controller migration

The supported runtime is now Python/FastAPI plus a customized React/TypeScript
RIMAPI Dashboard. RimWorld runs Harmony and RIMAPI; the old RimBot DLL must be
disabled to avoid two controllers. The retired mod's C# source, tests, and packaging
have been removed from the working tree; their history remains in Git at
`bf9a1eb`. The launcher starts the external runtime.

## Responsibilities

- RIMAPI owns game observations, definitions, notifications and game operations.
- The Python controller owns objectives, plans, specialist proposals,
  administrator arbitration, execution sequencing and outcome tracking.
- The React app supplies steering, plans, work, live activity, model progress,
  camera feed, settings and the upstream colony inspectors.
- SQLite stores colony-specific state and activity under `.rimbot/`.

All model traffic is local OpenAI-compatible HTTP, with Qwen thinking expressed
as `on`/`off`. Inference streams incrementally. Truncated or disconnected model
responses cannot execute partial tool calls. Specialists query and propose;
only accepted proposals execute. Workforce follows preliminary approved labor
requests when staffing is requested, then a final administrator pass resolves
the resulting orders. A final pass cannot add a new labor grant.

Seasonal planning supplies today, week, season, year and a fuzzy longer horizon.
Daily planning updates the near horizons; steering preserves the existing plan
and triggers a reasoned review with a player-facing response. Routine reviews
default to a quarter game day and events can trigger earlier reviews. There is
no small tool/action quota or tool rotation. Repeated identical queries stop a
stalled specialist. Role ownership and conflicting pawn assignments are validated.
Material reservations are currently advisory estimates in proposals and
administrator context, not native engine reservations.

## State, actions and failures

Endpoint schemas come from the pinned C# controllers and DTOs. The adapter
preserves RIMAPI naming and handles query-only, JSON and mixed requests. The
model can discover and describe endpoints and filter/page/sort their observations,
including sorting positions by distance. This avoids teaching a handcrafted set
of room/farm/stockpile recipes. Unsupported endpoints are explicit errors.

Every accepted action carries a state check. HTTP acceptance is recorded as
issued, not completed. Immediate operations such as allowing an item can complete
on the same call once observed. A lost HTTP response remains unknown until
reconciled; it is never blindly retried. Work records do not reserve coordinates
or block rebuilding after player changes. Removed tracking does not cancel game
orders. Unresolved tracking is reassessed after a game day.

Manual, steering, connection changes and save rollback invalidate old reviews.
Restart starts in Manual. Neither the controller nor the launcher unpauses the
game. No hostile-map-wide construction ban, blanket unforbid objective, pawn
deletion, forced weather, instant repairs, spawning or editor teleports are
exposed to model execution.

## Refresh and performance

The application maintains one RLE SSE connection and fans out events to clients.
The dashboard still uses HTTP for most measurements. Shared read results can be
reused for up to two seconds by dashboard consumers; authoritative action checks
bypass that cache. Writes and pushed events invalidate it. Immutable definition
queries are cached for the session and cleared on a colony/save boundary.

The upstream dashboard's hardcoded `map_id=0` calls are routed to the selected
map. Missing data and failed requests are reported rather than silently becoming
zeros. Background refresh keeps the last good data and never intentionally
unmounts the grid. Tests cover this behavior independently of RimWorld.

Further RIMAPI optimization should be measured in-game. Priorities are sharing
immutable definition serialization, bounded map queries using native indexes,
explicit freshness/revision fields, action-specific invalidation, and separating
HTTP filtering from cached DTO mutation. Do not cache mutable game state for a
season or rebuild a parallel simulation in Python.

## Camera

The Python service listens on an ephemeral loopback UDP port, configures RIMAPI's
camera, and relays complete JPEG frames as binary WebSocket messages. The default
is 1280×720 at 15 FPS. Slow browsers drop old frames rather than queue video.
The last viewer disconnecting stops our stream and restores its prior config.
An existing stream to another client is not taken over automatically.

Upstream UDP packets lack frame IDs. The receiver accepts ordered chunks and
discards malformed/incomplete sequences. A future RIMAPI protocol patch should
add frame IDs for unambiguous recovery. This feed shows the game camera, not
audio; it is not continuously passed to the model.

## Validation boundary

Controller tests use deterministic HTTP and model fixtures, including the whole
specialist/administrator/execute/reconcile loop. Frontend tests cover steering
keyboard behavior, preservation of draft text and stable refresh. Browser visual
inspection verifies the rendered app. These do not establish real-model gameplay
quality or validate every upstream inspector/action. A live RimWorld session is
still needed for the actual video feed and end-to-end colony behavior.

The full RLE benchmark runner and native blueprint/frame-ID lifecycle cache are
not yet ported into the Python runtime. The current executor uses fresh state
checks instead of redirecting retired action IDs. Keep these as explicit remaining
integration work, not a claim that protocol tests prove feature parity.
