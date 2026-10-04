# The launcher

[Documentation](../../README.md) · [System overview](overview.md)

The launcher (`RimGovernorLauncher.exe`, built from
[go/cmd/launcher](../../../go/cmd/launcher)) is the only player-facing surface:
it starts and stops the controller and the game, shows what the colony is doing
and offers the three player controls. It is a small WebView2 window around one
embedded page ([ui/index.html](../../../go/cmd/launcher/ui/index.html)) with no
web server and no front-end build. The page holds no planner and no labels or ordering of its own: every view is a
Go-side view model read through `Bind`ed functions.

## Why the page never calls serve

The page is loaded with `SetHtml`, so its origin is opaque, and `serve`
rejects a foreign `Origin`. The launcher process therefore calls `serve`
itself (`ServeClient`, [serveclient.go](../../../go/cmd/launcher/serveclient.go))
and hands the page typed readings. Each feed keeps its last good value: a failed
refresh returns that value marked stale with the error, and a 404 means "not
served" (Observe mode serves no routines, spectator or player routes), which is
a one-line notice rather than an error. A slow or failed refresh therefore never
replaces a useful view with an empty one. Poll cadences live in the client
(`StateEvery`, `NowEvery`, `RoutinesEvery`, `ClockEvery`); the page polls only
while its tab is showing and skips a tick while a call is in flight.

## Views

| Tab | Shows | Source |
| --- | --- | --- |
| Launch | Play, Restart, Stop and Close game; the saved game to load; component status rows (game layout, native mod, controller, game); the Log and Details; Settings; the operator controls. | Launcher state; build and controller output; `GET /api/player/*` for the controls. |
| Now | A connection header, then **Now** and **Development priorities**. | `GET /api/state`, `GET /api/spectator/now`, `GET /api/routines`. |
| Problems | The flight recorder as a filterable feed with kind counts, health and Copy. | `<profile>/flight/flight.jsonl` read in-process. |

[now.go](../../../go/cmd/launcher/now.go) builds the Now view models. **Now**
answers what the colony is doing at a glance (`GET /api/spectator/now`):

- the colony stage with the first unmet condition of the next and its
  measured values;
- the active concerns' progress records: method, the native observable it
  should move, the tick evidence last moved it, the review deadline and the
  status, the most urgent first;
- the pacing reason with the effective ticks per second: `running`,
  `tick_budget` between windows, `window_refused`, `held` while a clock event
  awaits review, `governor_off`, `stopped` or `cinematic`;
- the last clock stop with its latency split.

`internal/spectator` projects these from the last review's records and the
flight-recorder rows of the current launch. Reading issues no native call,
writes no journal row and requests no speed, so watching never changes the
simulation. **Development priorities** lists the optional projects the last
rounds ranked (comfort, research, production targets, defense, expansion) with
the capacity summary and why each waits; emergencies never appear in it.

The Problems tab ([problems.go](../../../go/cmd/launcher/problems.go)) reads the
flight recorder directly, so it still shows the last session after the
controller crashed or stopped. It hides decode rows by default; the controller's
own log and the build output stay on the Launch tab. See
[measure throughput](../testing/measure-throughput.md) for what the rows carry.

## Controls

[controls.go](../../../go/cmd/launcher/controls.go) owns the request ids and the
unresolved intents; the page only calls `botControl` and `ackClock` and renders
the `ControlsView`. A retry after an uncertain outcome reuses the same
`requestId`, never a new one. The controls are unavailable when the controller is
not running, runs in Observe mode or serves no player routes.

| Control | Route | Effect |
| --- | --- | --- |
| Resume | `POST /api/player/control/resume` | Runs the bot for the observed world. |
| Pause | `POST /api/player/control/pause` | Stops the bot; remains available regardless of an unresolved Resume. |
| Acknowledge inspected interruptions | `POST /api/player/clock/acknowledge` | Releases clock holds the player has inspected. |

An unresolved Resume (pending or uncertain) blocks another Resume until a later
journaled Pause supersedes it. The player routes are session, control,
control/pause and resume, clock, clock/acknowledge and colony; the player has no
other control surface, and player edits get no exemption from the autopilot. See the [player API](../contracts/go-player-api.md)
and [interface contracts](../contracts/interface-contracts.md).

## Help

The Launch tab shows the player-documentation URL as copyable text, not a link:
WebView2 would open a link inside the launcher window (replacing the page) and the
launcher binds no "open in browser" function.

## Telemetry

`serve` keeps a flight recorder under the profile by default
(`<profile>/flight/flight.jsonl`, an 8 x 8 MiB ring; `--flight-recorder <path>`
moves it, `--observe` has no profile and so none). The service does not serve it
over HTTP; the launcher and the acceptance harness read `flight.jsonl`
in-process through `bridge.TimelineReader`.
