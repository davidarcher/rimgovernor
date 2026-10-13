# Launcher

[Architecture](overview.md) · [Player controls](../../players/controls.md)

The Windows launcher is the C# project [tools/launcher](../../../tools/launcher):
a WinForms window hosting WebView2, published as one file,
`RimGovernorLauncher.exe`, by `RimGovernor.cmd`. It is its own project outside
the brain solution and references the acceptance harness
(`brain/src/RimGovernor.Brain.Acceptance`) for setup and case runs. The Go
launcher in `go/cmd/launcher` stays until #3102 removes it.

It embeds one WebView2 page (`ui/index.html`, an embedded resource) without a
web server or frontend build. C# owns the view models and the game's HTTP calls;
the page renders them through bound functions (`MainForm` defines each as a
global function that posts a message and returns a promise, so an error
surfaces in the page as `e.message`). The Evergreen WebView2 runtime is
assumed; when it is missing the launcher says so and exits.

## Data flow

```mermaid
flowchart LR
    UI[Embedded WebView2 page] --> Bind[MainForm bindings and view models]
    Bind --> Colony[New-colony client]
    Colony --> API[Brain HTTP channel in the game]
    Bind --> Process[Setup and game lifecycle]
```

The page is loaded with `NavigateToString` and has an opaque origin, so the
new-colony client makes its HTTP requests from C#. The game's address comes from
the `http-endpoint.json` file the in-process brain writes (address, pid, run id);
the launcher trusts it only when its pid is the game it started and
`/api/health` answers. Observation lives in Grafana.

The page polls only its visible tab and skips a tick while a request is in flight.

## Views

| Tab | Source and purpose |
| --- | --- |
| Launch | Game files and mod state, saved games, settings and colony generation; the Details log of build and start diagnostics; the observability stack toggle and dashboard links |
| Acceptance | Registered case list and one visible native run |

The launcher is the control surface; Grafana is the observation surface. The
tab-to-panel map is in the
[dashboards README](../../../tools/observability/dashboards/README.md).
While the stack is healthy the observability section shows one button per
dashboard (`Links.Dashboards` in [Links.cs](../../../tools/launcher/Links.cs));
`openLink` accepts only those exact URLs, the player guide and the Grafana root.
While the stack is stopped it shows a one-line hint instead.

## Controls

Play starts RimWorld from the private game copy with `-rimgovernor-inproc` and
the settings' extra arguments, then waits (up to five minutes) for the endpoint
file and a healthy channel. When the settings name a save to load, it is loaded
once the channel answers. There is no controller process: the brain runs inside
the game, so Stop and Close game both end RimWorld.

Settings are saved in `.rimgovernor/launcher.json`. Extra arguments and the save
to load act in the launcher. Observe, continue and layout overlay reach the
in-process host as the `RIMGOVERNOR_OBSERVE`, `RIMGOVERNOR_CONTINUE_STATE` and
`RIMGOVERNOR_LAYOUT_OVERLAY` environment variables of the game
([brain host](brain-host.md#hosting-in-the-mod-3115)); Observe also blocks colony
generation in the launcher. Telemetry is kept in the file but has no host input
yet: the host writes the OTLP files and has no endpoint to send to.
Journal selection and save behavior are described in
[save and resume](../../players/save-and-resume.md).

The window's bounds are kept in `.rimgovernor/launcher-window.json`, and the
game window's in `.rimgovernor/game-window.json` (RimWorld does not remember
its own).

## Acceptance tab

The case list is the registry linked into the launcher
(`RimGovernor.Brain.Acceptance.Cases`). Runs go through the acceptance command
in this process: its private root, a fresh output directory and
`-headless=false -no-series`. **From scratch** adds `-fresh`; otherwise a
failed run can resume from its checkpoint.

The tab refuses while the game is running. A run installs the required fixture
build; after the game closes, the poll restores the production build. Stop
cancels the run; Close game ends the game it left open. See the
[acceptance guide](../testing/acceptance-guide.md).

## New colony panel

[NewColony.cs](../../../tools/launcher/NewColony.cs) supplies the option lists,
ranges, DLC requirements and the Rhadamant preset; the page renders them without
inventing or filtering them. The runner
([NewColonyRunner.cs](../../../tools/launcher/NewColonyRunner.cs)) closes the
game, starts it afresh, posts `POST /api/lifecycle/new` and polls
`GET /api/lifecycle/new?requestId=`. Its elapsed timer advances between polls;
failed polls mark retained values stale. The form is validated by the same
`BridgeFunctions.ValidateNewColonyRequest` the game's route uses, and the spec
embeds the scenario parts, so settings, page and wire share one shape. The
part rows' choice lists live in the same file.

The launcher derives a unique save name from scenario, biome and seed. On
completion the page selects it in Saved game. Generation and cancellation
behavior are in [launch](../../players/launch.md#new-colony).
