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
| Launch | Play, Restart, Stop and Close game; the saved game to load; component status rows (game layout, native mod, controller, game); Settings; the operator controls; the collapsible New colony panel. | Launcher state; `GET /api/player/*` for the controls. |
| Now | A connection header, a one-line headline and the four report sections (Doing, Pursuing, Concerns, Waiting). | `GET /api/state`, `GET /api/spectator/now`, `GET /api/routines`. |
| Problems | The flight recorder as a filterable feed with kind counts, health and Copy. | `<profile>/flight/flight.jsonl` read in-process. |
| Log | The newest run's WARN and ERROR rows plus the player-facing INFO kinds, and the build output and controller stderr path on a failed start (Details). Polls only while the tab shows. | Launcher state; the newest run's flight rows. |

[now.go](../../../go/cmd/launcher/now.go) builds the Now view models: `headerView`
for the strip (connection, tick, paused, colony) and `reportView` for the
report (#2033). `reportView` joins `GET /api/spectator/now` with the development
slice of `GET /api/routines` and produces every word Go-side: a one-line
headline (stage, whether the governor runs, the last review's age, an emergency
when one is in force) and four sections the page prints in the order given,
with a flag (`warn`, `emergency`) per line and no labels of its own.

- **Doing**: the current method of the most urgent active concern, the native
  observable it should move and how long ago the tick evidence last moved it
  (game hours); when the pacing reason is idle (governor off, held, stopped,
  between windows, refused), that reason instead.
- **Pursuing**: the colony stage, its next unmet condition and current Concern methods.
- **Concerns**: every active Concern with its method, status, deficit and review
  deadline, most urgent first; the last clock stop with its latency split.
- **Waiting**: actual Concern blockers and waits, including prerequisites,
  unavailable methods and emergency precedence.

`internal/spectator` projects these from the last review's records and the
flight recorder. `/api/routines` supplies the review tick, emergency needs and
progress. These reads issue no native calls and change no simulation state.
A failed reading retains its last good report with a stale notice. In Observe
mode the routes are unserved and the tab shows a notice without sections.

The Problems tab ([problems.go](../../../go/cmd/launcher/problems.go)) reads the
flight recorder directly, so it still shows the last session after the
controller crashed or stopped. One background refresh reads and summarizes the
recorder at a time; Problems bindings return the last completed feed and Copy
snapshot without waiting for disk or aggregation. Filters take effect in the
next completed refresh. The Log tab shows the
newest run's flight rows through [logview](../../../go/internal/logview): WARN and ERROR rows and an explicit set of INFO event kinds, repeats collapsed. See
[measure throughput](../testing/measure-throughput.md) for what the rows carry.

## Acceptance tab

The Acceptance tab ([acceptance.go](../../../go/cmd/launcher/acceptance.go),
[acceptance_windows.go](../../../go/cmd/launcher/acceptance_windows.go)) lists
the registered native acceptance cases and runs one on demand in a visible game
window, for local playtesting. It does not link the case packages: the case
list is `go run ./internal/nativeaccept/cmd/acceptance list` (read once per
launcher process; a landing that adds cases restarts the launcher) and a run is
`acceptance run <case> -root <launcher root> -headless=false -no-series`
with the launcher's own controller binary as `-rimgovernor` and a fresh output
directory under `bridge/acceptance/launcher/`. From scratch adds `-fresh`;
otherwise a case that failed last time resumes from its checkpoint. The run
shares the launcher's game copy, so it refuses while the controller or the game
runs, and Play, setup and the mod rebuild stand down while it does. The run's
preflight installs the fixture build of the mod; the launcher sees it as stale
and restores the production build once the game closes. Stop ends the run's
process tree; the game it opened stays up until Close game.

## New colony panel

The Launch tab's New colony panel is collapsed by default, shows the generation state and phase in its summary while collapsed, opens itself when a generation starts (or is already running on load) and never re-collapses mid-run; the open state is not persisted. It renders `getNewColony()` (`NewColonyView` in
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

The form has no save-name field. `Generate` derives the name in Go
(`DeriveSaveName`): `RimGovernor-<scenario>-<biome, any or multi>-<seed slug>`
within the save-name pattern, with `-2`, `-3`, ... appended when the launcher's
saves folder (`ListSaves`, case-insensitive) already holds that name, so a
generate never overwrites a save. The name travels in the wire spec as before
(any caller-sent name is replaced) and shows in the progress as `saveName`.
Map size and planet coverage are option lists (`mapSizes`, `planetCoverages`)
copied from RimWorld's own pages (`Dialog_AdvancedGameConfig.MapSizes` 200 to
325 in steps of 25; `Page_CreateWorldParams.PlanetCoverages` 0.3, 0.5, 1; the
350/400 and 0.05 values are native test and dev-mode only). Temperature band,
world temperature and planet coverage sit in a collapsed Advanced section.

## ControlsThe launcher has no bot or clock controls. In Autopilot it passes `serve --resume`, so thebot runs on every load, and serve runs every window at Ultrafast whatever speedthe native controls show; `--follow-player-speed` (acceptance harnesses) opts backinto the player's own speed (#875). Clock holds are not acknowledged from thelauncher. The player has no other control surface, and player edits get noexemption from the autopilot.
## Help

The Launch tab shows the player-documentation URL as copyable text, not a link:
WebView2 would open a link inside the launcher window (replacing the page) and the
launcher binds no "open in browser" function.

## Telemetry

`serve` keeps a flight recorder under the profile by default
(`<profile>/flight/flight.jsonl`, a 16 x 32 MiB ring, 512 MiB; `--flight-recorder <path>`
moves it, `--observe` has no profile and so none). The service does not serve it
over HTTP; the launcher and the acceptance harness read `flight.jsonl`
in-process through `bridge.TimelineReader`.
