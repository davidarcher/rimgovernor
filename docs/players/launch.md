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
Controller output goes to `.rimgovernor/go/controller-<stamp>.{out,err}.log`.

## Settings

Settings persist to `.rimgovernor/launcher.json`.

- **Main**: Autopilot or Observe only (no writes); continue the last state
  database or start fresh. In Autopilot the bot starts on every load and always
  runs at Ultrafast (adaptive: paces ticks to your frame rate).
- **Colony policy**: allow slaughter or release of surplus animals, the layout
  overlay, and food reserve days.
- **Advanced**: debug logging and extra `rimgovernor serve` arguments
  (`rimgovernor serve -h` lists them).

## Related reading

[Sessions and recovery](../developers/architecture/sessions-and-recovery.md) · [Save and
resume](save-and-resume.md)
