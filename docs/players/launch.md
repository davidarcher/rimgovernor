# Launch a colony

[Documentation](../README.md)

Double-click `RimGovernor.cmd` (see [setup](setup.md)). When every
status row is OK, press **Play**: the launcher starts the Go controller, which
starts RimWorld, and shows **Running** once the controller answers.
Enable Run in background in RimWorld.

Pick a **Saved game** above Play to load it automatically once the controller is
up (newest first; "Main menu" loads nothing). With none selected the game boots
to its main menu; load a save there.

- **Stop** stops the controller only; the game keeps running.
- **Restart** stops and starts the controller with the current settings.
- **Close game** closes the RimWorld started from `.rimgovernor/native-rimworld`.

Play stops an earlier controller from this checkout first. The controller
starts at port 8787 and moves up past ports another checkout is using.
Controller output goes to `.rimgovernor/go/controller-<stamp>.{out,err}.log`; the **Log** tab shows the controller's log events and the build output.

## New colony

API clients can request governor ideology selection with
`governorIdeoligion: true` in the new-colony spec. With Ideology installed,
the controller chooses a fluid design that avoids known work restrictions and
mood costs. An explicit `ideoligion` design takes precedence. Unread or
unsupported required facts stop creation with an explanation.
Auto's `ideoligion-reform` family considers a safe, game-authorized reform when
a supported plain precept change improves those costs without losing benefits.

The **New colony** panel on the Launch tab (collapsed until you open it; it opens itself when a generation starts) generates a fresh colony without
touching the RimWorld menus: pick the scenario, colonist count, seed (blank is
random; **Random** fills one in), biomes, difficulty, storyteller (including
Quiet), map size and flat tile, then press **Generate**. **Advanced** (collapsed)
holds the temperature band, world temperature and planet coverage. Map size and
planet coverage offer the same choices as RimWorld's own world creation. The
save is named automatically (`RimGovernor-<scenario>-<biome>-<seed>`, with `-2`,
`-3` added if that name exists, so nothing is overwritten) and the panel shows
the name. Options marked "needs <DLC>" require that expansion; the game refuses
one it lacks.

**Scenario parts** (collapsed, below Advanced) sets parts on the base
scenario, as the game's scenario editor does. A kind you fill in (say starting
things) replaces the base scenario's parts of that kind; a kind you leave empty
keeps them. Kinds: starting things (def, count,
start with, start near or scattered), forced traits and hediffs (chance and who
they apply to), permanent game conditions, disallowed buildings,
disabled incidents, and overrides for the player faction and arrival method.
Colonists sets the pawn count. Inputs suggest common Core defs but take any
defName; the game refuses an unknown one. The launcher refuses out-of-range or
incomplete rows before it stops anything. The parts are saved with the rest of
the form.

**Preset** (top of the panel) fills every row at once. *Rhadamant's Sea Ice
challenge* is Crashlanded with one colonist (Sanguine, Super-immune, Jogger), a
permanent cold snap, Randy Random on Extreme, an ice sheet on a very cold world,
a starting stockpile (800 silver, 50 survival meals, 300 wood, 450 steel, 30
components, 30 medicine, a rifle) and 720 steel, 7 survival meals and 3 ship
chunks scattered. The seed is pinned but editable; edit any row afterwards, or
pick **Custom** to return to the defaults.

Generation closes the running game and controller, restarts them, builds the
world and saves the colony, so it needs Autopilot (Observe only shows a notice
instead). The panel shows the state, the current phase, a timer, the reroll
count and, if the controller stops answering, the last values marked as stale.
**Cancel** closes the game and reports cancelled. When it completes, the save is
selected in **Saved game**; press **Play** to load it. See [save and
resume](save-and-resume.md#new-colony-saves).

## Settings

Settings persist to `.rimgovernor/launcher.json`.

- **Main**: Autopilot or Observe only (no writes); continue the last state
  database or start fresh. In Autopilot the bot starts on every load and always
  runs at Ultrafast (adaptive: paces ticks to your frame rate).
- **Colony policy**: allow slaughter or release of surplus animals, the layout
  overlay, and food reserve days.
- **Send telemetry to local collector**: Play passes `--otlp-endpoint` to the
  controller. The launcher page's **Observability** section starts and stops the
  optional Docker stack ([developer guide](../developers/observability.md)),
  shows stopped/starting/healthy, and opens Grafana and its Now, Concern
  transitions, Problems and Log dashboards (the launcher no longer has those
  tabs). While the stack is stopped the section shows a one-line hint. It needs
  Docker; without it the section says so and Play is unaffected. See
  [controls](controls.md#watch-the-governor-in-grafana).
- **Advanced**: extra `rimgovernor serve` arguments
  (`rimgovernor serve -h` lists them).

## Related reading

[Sessions and recovery](../developers/architecture/sessions-and-recovery.md) · [Save and
resume](save-and-resume.md)
