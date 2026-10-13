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
  tool's ProtoJSON request (empty means `{}`), the reply body is the ProtoJSON payload, and the tool's timing
  comes back in `X-RimGovernor-Timing`. Optional `X-RimGovernor-Class` and `X-RimGovernor-Trace` request headers are
  the `class` and `trace` arguments. A tool failure inside the proto reply is a 200 with the failure arm, as over
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
| `RimGovernor.Brain.PlayerApi` | no: the brain loads it (#3082) | The provider interfaces (`IPlayerControl`, `IControlReader`, `IClockReview`) and the player routes over them. Depends on Store and Domain. |

The mod side is `HttpChannelHost.cs` in the Bridge: `NativeToolInvoker` reflects the `[Tool]` methods whose only
argument is `request` and runs them with a context carrying the caller's arguments and the host's game-thread
access, so GABP and HTTP share one implementation; `HttpChannelBootstrap` (a module initializer) starts the server
when `RIMGOVERNOR_OTLP_DIR` is set (the directory of the OTLP rings; `RIMGOVERNOR_RUN_ID` optionally names the run).
Unset, nothing listens.

The brain host (#3082) registers PlayerApi's routes with `PlayerRoutes.Register(routes, player, controls, clock)`
on the shared `RouteTable`, and disposes the result when it reloads. Routes whose provider is null are not
registered.

## Ported and not yet ported

Ported: the channel, `/api/health`, `/native/<tool>`, and the player `session` (just `mode`), `control`
(GET, resume, pause) and `clock` (GET, acknowledge) routes, with Go's strict request decoding.

Not yet ported (still Go-only): `/api/state`, `/api/plan`, `/api/routines`, `/api/player/colony`, the
presentation and notification reads, and the lifecycle save, load and new-colony routes with their request
validation. Their provider interfaces (`SnapshotProvider`, `PlanReader`, `RoundsProvider`, `ColonyStatus`,
`LifecycleWriter`) wait on the red-until-#3111 types they return (`ColonyStatusReport`, `ControlState`, the
observation identity). `PlayerState` is the small projection of `ControlState` the control route shows; the brain
host adapts one to the other.
