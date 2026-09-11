# Dashboard and controls

[Player guide](README.md)

| View | Use it to |
| --- | --- |
| Watch | See the game and send chat requests. |
| Priorities | Inspect colony needs, policy and stability checks. |
| Work | Follow plans, progress and blockers. |
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

Chat needs the configured local model in LM Studio. Ask for an outcome, such as
building a room, selecting research or maintaining a food target. Accepted
requests appear in the same plan as automated work. Pawns still need to carry
out the orders, so check Work for completion and blockers.

Manual permits explicit player requests while routine automation stays off.
Cancelling a goal stops further pursuit but leaves issued game orders in place.
Removing pending construction is a separate request; completed buildings remain.

Use **Save checkpoint and pause** before ending an owned session. See
[save and resume](save-and-resume.md) for restarting it.
