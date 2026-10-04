# Dashboard and controls

[Player guide](README.md)

| View | Use it to |
| --- | --- |
| Watch | Pause or resume the colony, see what it is doing now. |
| Work | Follow plans, progress, blockers and development priorities. |
| Colony | Inspect colonists, jobs, skills, gear, health and mood. |
| Governor | Follow the controller's live telemetry. |
| Help | Read the player guide, launch options and save instructions. |

## Automate or take control

The dashboard starts in **Manual**. **Automate** pauses for an initial review,
then resumes supervised play. Routine control uses no model calls.

Player time controls enter Manual. **Take control** stops automation before
accepting dashboard input. Releasing control leaves the colony in Manual until
you explicitly resume automation.

Under automation, every colony's first shelter is a rectangular room; cramped
terrain gets an L-shaped, two-chamber or irregular room that fits the ground.

Headless sessions have no game images.
Viewing a colony does not take control of it.

## Give a request

The autopilot reads policy in its next round, so check Work for the result.

Manual permits explicit player requests while routine automation stays off.
Removing pending construction is a separate request; completed buildings remain.

## Read development priorities

Work lists the optional projects the last rounds ranked: comfort, research,
production targets, defense and expansion. Each row shows whether the project was
selected, is in progress, or why it waits - for capacity, for free pawns of a named
work type, for a known deficit, or because outdoor work is unsafe. Emergencies are
handled before this list and never appear in it. The panel is absent when routine
reviews are disabled.

Use **Save checkpoint and pause** before ending an owned session. See
[save and resume](save-and-resume.md) for restarting it.
