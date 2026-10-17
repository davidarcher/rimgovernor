# Local HTTP/JSON control channel

See also: [native facade](native-facade.md), [Player API](../contracts/player-api.md).

The loopback-only HTTP/JSON channel (#3083, epic #3043). It is a
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
  'none'` CSP. (`cross-site` and `same-site` are both refused.)
- **Routes.** `POST /native/<op>` is a test-only route for the out-of-process acceptance harness. An op is a
  `NativeOps` entry (brain `RimGovernor.Brain.NativeFacade`), named without the `rimgovernor/` prefix: the body is its JSON
  request record (empty means `{}`), deserialized with `WireJson` (a body that does not fit is a 400 `invalid_request`
  with the deserializer's message), and the reply body is its JSON reply record. A namespaced name
  (`POST /native/test/lab_start`) or any other name routes to the host's tool registry (the host's lifecycle tools and the
  `test/*` fixtures): the body is the JSON object of the named arguments and a non-object result is wrapped as
  `{"result": ...}`. `GET /api/tools` lists the routable names (`{"tools":[...]}`, sorted; a `rimgovernor/` name is
  listed without its prefix). A refusal inside the reply (its failure arm) is a 200; an exception is a 500
  `tool_failed`; an unknown name is a 404. No headers carry timing, admission class or trace. Nothing streams over
  HTTP; frame and clock-event handoff is in-process ([native facade](native-facade.md)).
- **Failure shape.** `{"code","detail"}`. Bodies are bounded per route (8 MiB for `/native`, 8192 bytes for the
  player routes), responses at 16 MiB, handlers have a per-route timeout (5 minutes for `/native`, 30 s otherwise);
  chunked requests are refused with 411.
- **Access rows.** One `http_access` row per request (component `httpapi`, `WARN` on a 5xx) through the optional
  `TelemetryEmitter`; fast successful polled GETs are dropped, as in [otel-signals](../contracts/otel-signals.md).

## Projects

| Project | Ships with the mod | Holds |
| --- | --- | --- |
| `RimGovernor.Brain.HttpChannel` | yes, beside the other brain DLLs (with `Brain.Telemetry`) | `HttpChannelServer`, `RequestGuard`, `RouteTable`, `Dispatcher`, `EndpointFile`, `NativeRoutes` and `INativeOpInvoker`. Game-free. |
| `RimGovernor.Brain.PlayerApi` | no: the brain host loads it (#3082) | The provider interfaces (`IPlayerControl`, `ISnapshotProvider`, `ILifecycleWriter` and the rest, in `PlayerProviders.cs`) and the player, read, presentation and lifecycle routes over them. Depends on HttpChannel, Domain, Store, Policy, Facts, Observation, Bridge.Contracts, BuildingRuntime and Wire. |

The mod side is `HttpChannelHost.cs` in the Bridge: `NativeOpTable` is an explicit table, compile-checked by the
`NativeOps` entries. A facade op runs its `INativeFacade` method on the in-process facade, the same typed call the brain
makes; the harness's pull read of the newest frame (`NativeOps.ReadSnapshot`) runs `SnapshotStream.Read` in a game-thread
hop; the acceptance-only reads (`AcceptanceReadTools.Ops`: colony facts, blight, starter site, Impressiveness,
capture profile, animal races) take a JSON object of named arguments and answer `{"payload": "<json>"}`; nothing is
found by reflection. `HttpChannelBootstrap.Start`, called once by `NativeStartup`, starts the server when
`RIMGOVERNOR_OTLP_DIR` is set (the directory of the OTLP rings; `RIMGOVERNOR_RUN_ID` optionally names the run). Unset,
nothing listens. The channel binds only after the host has built its registry (`RimBridgeToolAccess.Published`, on the
game thread once play data has loaded), so the registry fallback and the facade's one-time installs are ready.
The brain host (#3082) registers PlayerApi's routes on the shared `RouteTable` and disposes the results when it
reloads. A route family whose provider is null is not registered, so it answers 404:

| Call | Routes | Providers |
| --- | --- | --- |
| `PlayerRoutes.Register(routes, player, controls, clock)` | `GET /api/player/session`, `control`, `POST control/resume`, `control/pause`, `GET clock`, `POST clock/acknowledge` | `IPlayerControl`, `IControlReader`, `IClockReview` |
| `ReadRoutes.Register(routes, snapshots, plans, rounds, colony)` | `GET /api/state`, `/api/plan?id=`, `/api/routines`, `/api/player/colony` | `ISnapshotProvider`, `IPlanReader` (both required), `IRoundsProvider`, `IColonyStatusReader` |
| `PresentationRoutes.Register(routes, snapshots, reader, media, notifications)` | `GET /api/presentation/camera`, `selection`, `colonists`, `render-state`, `notifications`; `POST render-demand` | `IPresentationReader`, `IPresentationMediaWriter`, `INotificationReader` |
| `LifecycleRoutes.Register(routes, snapshots, lifecycle)` | `POST` and `GET ?requestId=` on `/api/lifecycle/save`, `load`, `new`, `menu` | `ILifecycleWriter` |

## Behavior carried over from the brain, and where it differs

- Read bodies are the brain DTOs (camelCase, unknown facts null, 64-bit counters as strings). Where the brain wrote a null
  list the C# routes write an empty array. A plan action other than a building or an owned draft is projected
  without a payload instead of failing the read.
- A presentation read answers only for a connected, fresh, valid world identity, passes the reply through
  `ValidateContext`, requires the reply's context to name that identity at or after its tick, re-reads the
  identity afterwards and refuses a changed world, a reply with unknown fields or a non-finite number, and one
  over 1 MiB. Any refusal is 503 `unavailable`.
- `render-demand` and the lifecycle mutations carry no token: The brain gated them behind `X-RimGovernor-Player`; the
  decision of #3083 is the request guard alone.
- Lifecycle: save needs a fresh identity and manual mode (409 otherwise) and answers 201 with the completed
  checkpoint; load answers 201 completed, 202 pending, 409 superseded; new colony answers 201 completed, 202
  pending, 409 superseded or the native refusal as `native_<code>` (400, 404, 409 or 502); the return to the main
  menu (`menu`, [player API](../contracts/player-api.md#return-to-the-main-menu)) answers 201 completed or 202
  pending and maps a refusal the same way. The GET recovery reads
  answer 200 for completed and pending. `ILifecycleWriter` returns the native reply as it is; the retry after a
  blocking-attention acknowledgement is the writer's, not the route's. Request bodies are `StrictJson`-checked
  (duplicate keys and nesting beyond 8 are refused, as on the player routes), and field names match exactly,
  where the brain matched them case-insensitively. A new-colony body is decoded into the wire request and validated by
  `BridgeFunctions.ValidateNewColonyRequest`; `ScenarioParts.ApplyRhadamant` is the port of `RhadamantParts`.
- `ReadTally` (`Bridge.Contracts`) counts the native reads of one scope in an ambient tally; the clock scheduler
  opens the scope with `BridgeFunctions.WithReadTally()` and the native call layer calls
  `BridgeFunctions.CountNativeRead(tool)` (the #3082 host wires that call).

`PlayerState`, `ControllerSnapshot` and `RoundsStatus` are the small projections the routes show; the brain host
builds them from its own control state.
