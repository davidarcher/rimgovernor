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

Save before stopping if you want to keep current progress.

- **Stop** ends the brain and leaves RimWorld running.
- **Restart** restarts the brain with current settings.
- **Close game** closes the launcher's private game.
- **Play** starts the brain and loads the selected save, if any.

The launcher enables Auto at startup and after each load. Pause in RimWorld
to take manual control.

**Continue last state** selects the latest brain journal only when it
records a running session; otherwise the launcher selects a fresh journal.
This setting does not select a colony timeline: the loaded RimWorld save does.

The brain flushes its intent when the game saves. After a crash, the last
flushed intent is kept and unsaved brain intent can be lost. On load, the brain rechecks the world before issuing work.

