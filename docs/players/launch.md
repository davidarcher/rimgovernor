# Launch a prepared colony

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
- **Advanced**: extra `rimgovernor serve` arguments
  (`rimgovernor serve -h` lists them).

## Related reading

[Sessions and recovery](../developers/architecture/sessions-and-recovery.md) · [Save and
resume](save-and-resume.md)
