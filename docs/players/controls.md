# Dashboard and controls

[Player guide](README.md)

| View | Use it to |
| --- | --- |
| Watch | See the game and send chat requests. |
| Priorities | Inspect colony needs, policy and stability checks. |
| Work | Follow plans, progress, blockers and development priorities. |
| Colony | Inspect colonists, jobs, skills, gear, health and mood. |
| Help | Read the player guide, launch options and save instructions. |

## Automate or take control

The dashboard starts in **Manual**. **Automate** pauses for an initial review,
then resumes supervised play. Routine control uses no model calls.

Player time controls enter Manual. **Take control** stops automation before
accepting dashboard input. Releasing control leaves the colony in Manual until
you explicitly resume automation. Direct pointer and keyboard input is available
in supported rendered Docker sessions; desktop sessions offer camera and
colonist selection controls.

Video can pause while the game runs. Headless sessions have no game images.
Viewing a colony does not take control of it.

## Give a request

Chat needs the configured local model in LM Studio. Ask what the autopilot is
doing and why, or nudge it: activate or cancel a maintained goal, cap the
population, set expedition limits, decide for a named pawn, or reserve or
restrict a resource. Each reply explains, and shows the one policy change it
applied, if any. Chat never places buildings or issues orders; the autopilot
reads the changed policy on its next review, so check Work for the result.

Manual permits explicit player requests while routine automation stays off.
Cancelling a goal stops further pursuit but leaves issued game orders in place.
Removing pending construction is a separate request; completed buildings remain.

## Read development priorities

Work lists the optional projects the last routine review ranked: comfort, research,
production targets, defense and expansion. Each row shows whether the project was
selected, is in progress, or why it waits — for capacity, for free pawns of a named
work type, for a known deficit, or because outdoor work is unsafe. Emergencies are
handled before this list and never appear in it. The panel is absent when routine
reviews are disabled.

Use **Save checkpoint and pause** before ending an owned session. See
[save and resume](save-and-resume.md) for restarting it.
