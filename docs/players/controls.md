# The launcher and controls

[Player guide](README.md)

Everything you do happens in the launcher window.

| Tab | Use it to |
| --- | --- |
| Launch | Play, stop or restart the controller, pick a saved game, change settings, and use the three controls below. |
| Now | See what the colony is doing now and which development projects are queued. |
| Problems | Read the controller's problem log; copy rows to share. |

## Three controls

The controls appear on the Launch tab once the controller runs in Autopilot mode
(Observe only has none).

- **Resume** starts the autopilot for the colony that is loaded. Routine control
  uses no model calls.
- **Pause** stops it. The game keeps running; the colony simply stops being
  managed until you Resume.
- **Acknowledge inspected interruptions** releases the controller after a pause
  for a clock event that you have looked at.

By default the controller starts paused; turn on **Start the autopilot as soon as
a colony loads** in Settings to resume automatically on every load. If a control's
outcome is uncertain, the button offers a retry that repeats the same request.

Under automation, every colony's first shelter is a rectangular room; cramped
terrain gets an L-shaped, two-chamber or irregular room that fits the ground.

Headless sessions have no game images.

## Read the Now tab

**Now** shows the colony stage, the current concerns with their method, expected
result and review deadline, the pacing reason and the last clock stop.

**Development priorities** lists the optional projects the last rounds ranked:
comfort, research, production targets, defense and expansion. Each row shows
whether the project was selected, is in progress, or why it waits - for capacity,
for free pawns of a named work type, for a known deficit, or because outdoor work
is unsafe. Emergencies are handled before this list and never appear in it. The
list is absent when routine reviews are disabled.

If a reading goes stale the tab keeps the last good value and says so.

Use **Stop** after pausing before ending an owned session. See
[save and resume](save-and-resume.md) for restarting it.
