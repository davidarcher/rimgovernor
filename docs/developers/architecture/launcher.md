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

## New colony panel

The Launch tab's New colony panel renders `getNewColony()` (`NewColonyView` in
[newcolony.go](../../../go/cmd/launcher/newcolony.go)) verbatim: option lists
with their labels, order and `requires` DLC, ranges, defaults, the last spec and
the generation progress. The page fills the form once from `spec`, calls
`generateNewColony(spec)` / `cancelNewColony()` and never filters or reorders an
option. It polls only while the Launch tab shows and skips a tick while a call is
in flight. `progress.elapsedMs` is the launcher's own clock; the page anchors on
each poll and ticks its timer locally, so the panel keeps moving while native is
busy. A failed poll marks the last good values stale. When a run goes from
active to completed while the page watches, the page selects the new save in the
Saved game picker (and so persists it as the load setting).

## ControlsThe launcher has no bot or clock controls. In Autopilot it passes `serve --resume`, so thebot runs on every load, and serve runs every window at Ultrafast whatever speedthe native controls show; `--follow-player-speed` (acceptance harnesses) opts backinto the player's own speed (#875). Clock holds are not acknowledged from thelauncher. The player has no other control surface, and player edits get noexemption from the autopilot.
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
