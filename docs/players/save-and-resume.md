# Save and resume

[Player guide](README.md)

Save normally in RimWorld. The save contains the governor's intent as well as
the colony, so loading it restores that timeline. Keep the native mod enabled.
You do not need to pair the save with a controller database.

## New colony saves

The launcher's [New colony](launch.md#new-colony) panel names its save
automatically: `RimGovernor-<scenario>-<biome>-<seed>`. It adds a numeric suffix
if the name exists. After generation, the new save is selected in **Saved game**.

## Restart a session

Save before stopping if you want to keep current progress.

- **Stop** ends the controller and leaves RimWorld running.
- **Restart** restarts the controller with current settings.
- **Close game** closes the launcher's private game.
- **Play** starts the controller and loads the selected save, if any.

The launcher enables Auto at startup and after each load. Pause in RimWorld
to take manual control.

**Continue last state** selects the latest controller journal only when it
records a running session; otherwise the launcher selects a fresh journal.
This setting does not select a colony timeline: the loaded RimWorld save does.

The controller flushes its intent when the game saves. If it is disconnected,
a save keeps the last flushed intent; unsaved controller intent can be lost
with a crash. On load, the controller rechecks the world before issuing work.

## Attached sessions

An attached controller reconnects to the same external game and private
profile. Keep that game running if you intend to reconnect to it.
