# Save and resume

[Player guide](README.md)

Save normally in RimWorld. The save contains the governor's intent as well as
the colony, so loading it restores that timeline. Keep the native mod enabled.
You do not need to pair the save with a brain database.

## New colony saves

The launcher's [New colony](launch.md#new-colony) panel names its save
automatically: `RimGovernor-<scenario>-<biome>-<seed>`. It adds a numeric suffix
if the name exists. After generation, the new save is selected in **Saved game**.

## Restart a session

The brain runs inside the game, so every button that ends the game ends the
brain with it. Save in RimWorld first if you want to keep current progress:
Stop, Restart and Close game do not save.

- **Stop** and **Close game** close the launcher's private RimWorld, and the
  brain with it. They are the same action.
- **Restart** closes the game the same way, then starts it again with the
  current settings and the selected save.
- **Play** starts the game and the brain and loads the selected save, if any.

The launcher enables Auto at startup and after each load. Pause in RimWorld
to take manual control.

**Continue last state** selects the latest brain journal only when it
records a running session; otherwise the launcher selects a fresh journal.
This setting does not select a colony timeline: the loaded RimWorld save does.

The brain flushes its intent when the game saves. If the brain takes more than
a few seconds to answer, the game saves anyway with the brain's last flushed
intent and notes a warning in the log; your save is never held up by the brain.
After a crash, the last flushed intent is kept and unsaved brain intent can be lost. On load, the brain rechecks the world before issuing work.

