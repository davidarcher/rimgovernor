# The launcher and controls

[Player guide](README.md)

Everything you do happens in the launcher window.

| Tab | Use it to |
| --- | --- |
| Launch | Play, stop or restart the controller, pick a saved game, change settings, and change settings. |
| Now | Read the governor's last report on the colony: what it is doing, pursuing, worried about and waiting on. |
| Problems | Read the controller's problem log; copy rows to share. |

## Speed and the bot

Pressing Play runs the autopilot on whatever colony loads, and again after every
load. The bot always runs the game at maximum speed; the speed buttons inside
RimWorld do not change it. If you pause the game natively, the bot waits.

## Read the Now tab

**Now** opens with the connection strip and a one-line headline (the colony stage,
whether the governor is running and how long ago it last reviewed the colony),
then four sections:

- **Doing**: the method the most urgent concern is using, the in-game number it
  should move and how long ago that number last moved. When nothing is being
  worked it says why: governor off, held for a review, stopped, or between
  windows.
- **Pursuing**: the colony stage, what the next stage is waiting on, and the
  Concern methods currently in progress.
- **Concerns**: the active concerns with their method, status and review
  deadline, most urgent first; an emergency is marked. The last clock stop and
  how long it took to land close the section.
- **Waiting**: the Concerns waiting on prerequisites, unavailable methods,
  native blockers or emergency precedence. Pawn work priorities schedule queued
  work; the governor does not allocate exclusive development slots.

In Observe mode the controller serves no colony readings, so the tab shows a
single notice instead of the report.

If a reading goes stale the tab keeps the last good value and says so.

Use **Stop** after pausing before ending an owned session. See
[save and resume](save-and-resume.md) for restarting it.
