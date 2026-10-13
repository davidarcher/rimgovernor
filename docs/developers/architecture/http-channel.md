# Local HTTP/JSON control channel

See also: [native facade](native-facade.md), [Go player API](../contracts/go-player-api.md).

The C# port's replacement for the Go `httpapi` server (#3083, epic #3043). It is a loopback-only HTTP/JSON
listener (`HttpListener`) the mod hosts, with route modules the brain publishes into. Settled decisions
(issue #3083, 2026-10-13):

- **Discovery.** The server binds `127.0.0.1` on an ephemeral port; there is no fixed port. It writes
  `http-endpoint.json` next to the OTLP rings (`otlp/`): `{"schemaVersion":1,"address":"http://127.0.0.1:PORT",
  "pid":N,"runId":"..."}`, atomically (temp file, then replace). A client polls the file; `runId` tells a stale file of
  an earlier run from the live one. A stopping server removes the file only if it still names its own run.
- **Auth.** Host and origin checks only; there is no token. `RequestGuard` is the one place that decides:
  the Host must be `localhost`, `127.0.0.1` or `[::1]` (valid port or none), the target at most 2048 bytes, any
  `Sec-Fetch-Site` must be `none` or `same-origin`, any `Origin` must be exactly `http://<the Host header>`, and a
  POST must be `application/json`. Every reply carries `Cache-Control: no-store`, `nosniff` and a `default-src
  'none'` CSP. (Go refused only `cross-site`; `same-site` is refused here too.)
- **Routes.** `POST /native/<tool>` mirrors the GABP tool names without the `rimgovernor/` prefix. The body is the
  tool's JSON request record (empty means `{}`), the reply body is the JSON reply record (`WireJson`), and the tool's timing
  comes back in `X-RimGovernor-Timing`. Optional `X-RimGovernor-Class` and `X-RimGovernor-Trace` request headers are
  the `class` and `trace` arguments. A tool failure inside the reply (its failure case) is a 200 with the failure arm, as over
  GABP; an exception is a 500 `tool_failed`; an unknown tool is a 404. Only tools whose sole argument is `request`
  have a route. Frame and clock-event streaming stays on GABP and the ring (#3102); nothing streams over HTTP.
- **Failure shape.** `{"code","detail"}`. Bodies are bounded per route (8 MiB for `/native`, 8192 bytes for the
  player routes), responses at 16 MiB, handlers have a per-route timeout (5 minutes for `/native`, 30 s otherwise);
  chunked requests are refused with 411.
- **Access rows.** One `http_access` row per request (component `httpapi`, `WARN` on a 5xx) through the optional
  `TelemetryEmitter`; fast successful polled GETs are dropped, as in [otel-signals](../contracts/otel-signals.md).

## Projects

| Project | Ships with the mod | Holds |
| --- | --- | --- |
| `RimGovernor.Brain.HttpChannel` | yes, beside the other brain DLLs (with `Brain.Telemetry`) | `HttpChannelServer`, `RequestGuard`, `RouteTable`, `Dispatcher`, `EndpointFile`, `NativeRoutes` and `INativeToolInvoker`. Game-free. |
| `RimGovernor.Brain.PlayerApi` | no: the brain loads it (#3082) | The provider interfaces (`IPlayerControl`, `ISnapshotProvider`, `ILifecycleWriter` and the rest, in `PlayerProviders.cs`) and the player, read, presentation and lifecycle routes over them. Depends on HttpChannel, Domain, Store, Policy, Facts, Observation, Bridge.Contracts, BuildingRuntime and Wire. |

The mod side is `HttpChannelHost.cs` in the Bridge: `NativeToolInvoker` reflects the `[Tool]` methods whose only
argument is `request` and runs them with a context carrying the caller's arguments and the host's game-thread
access, so GABP and HTTP share one implementation; `HttpChannelBootstrap` (a module initializer) starts the server
when `RIMGOVERNOR_OTLP_DIR` is set (the directory of the OTLP rings; `RIMGOVERNOR_RUN_ID` optionally names the run).
Unset, nothing listens.

The brain host (#3082) registers PlayerApi's routes on the shared `RouteTable` and disposes the results when it
reloads. A route family whose provider is null is not registered, so it answers 404:

| Call | Routes | Providers |
| --- | --- | --- |
| `PlayerRoutes.Register(routes, player, controls, clock)` | `GET /api/player/session`, `control`, `POST control/resume`, `control/pause`, `GET clock`, `POST clock/acknowledge` | `IPlayerControl`, `IControlReader`, `IClockReview` |
| `ReadRoutes.Register(routes, snapshots, plans, rounds, colony)` | `GET /api/state`, `/api/plan?id=`, `/api/routines`, `/api/player/colony` | `ISnapshotProvider`, `IPlanReader` (both required), `IRoundsProvider`, `IColonyStatusReader` |
| `PresentationRoutes.Register(routes, snapshots, reader, media, notifications)` | `GET /api/presentation/camera`, `selection`, `colonists`, `render-state`, `notifications`; `POST render-demand` | `IPresentationReader`, `IPresentationMediaWriter`, `INotificationReader` |
| `LifecycleRoutes.Register(routes, snapshots, lifecycle)` | `POST` and `GET ?requestId=` on `/api/lifecycle/save`, `load`, `new` | `ILifecycleWriter` |

## Behavior carried over from Go, and where it differs

- Read bodies are the Go DTOs (camelCase, unknown facts null, 64-bit counters as strings). Where Go wrote a null
  list the C# routes write an empty array. A plan action other than a building or an owned draft is projected
  without a payload instead of failing the read.
- A presentation read answers only for a connected, fresh, valid world identity, passes the reply through
  `ValidateContext`, requires the reply's context to name that identity at or after its tick, re-reads the
  identity afterwards and refuses a changed world, a reply with unknown fields or a non-finite number, and one
  over 1 MiB. Any refusal is 503 `unavailable`.
- `render-demand` and the lifecycle mutations carry no token: Go gated them behind `X-RimGovernor-Player`; the
  decision of #3083 is the request guard alone.
- Lifecycle: save needs a fresh identity and manual mode (409 otherwise) and answers 201 with the completed
  checkpoint; load answers 201 completed, 202 pending, 409 superseded; new colony answers 201 completed, 202
  pending, 409 superseded or the native refusal as `native_<code>` (400, 404, 409 or 502). The GET recovery reads
  answer 200 for completed and pending. `ILifecycleWriter` returns the native reply as it is; the retry after a
  blocking-attention acknowledgement is the writer's, not the route's. Request bodies are `StrictJson`-checked
  (duplicate keys and nesting beyond 8 are refused, as on the player routes), and field names match exactly,
  where Go matched them case-insensitively. A new-colony body is decoded into the wire request and validated by
  `BridgeFunctions.ValidateNewColonyRequest`; `ScenarioParts.ApplyRhadamant` is the port of `RhadamantParts`.
- `ReadTally` (`Bridge.Contracts`) counts the native reads of one scope in an ambient tally; the clock scheduler
  opens the scope with `BridgeFunctions.WithReadTally()` and the native call layer calls
  `BridgeFunctions.CountNativeRead(tool)` (the #3082 host wires that call).

`PlayerState`, `ControllerSnapshot` and `RoundsStatus` are the small projections the routes show; the brain host
builds them from its own control state.
