# Local RIMAPI changes

The RIMAPI submodule includes a local commit based on upstream v1.10.0
(`dfa4b2909e132081898845d0c4936fcefa86c91c`). Nothing has been pushed.
The patch is reviewable source; the bundle preserves the exact commit so a new
checkout can resolve the submodule without relying on an unpublished remote.

If a recursive submodule update cannot fetch the local commit, initialize the
upstream repository, import the bundle, then retry the checkout:

```powershell
git submodule init
git clone https://github.com/IlyaChichkov/RIMAPI.git integrations/RIMAPI
git -C integrations/RIMAPI fetch ../patches/rimapi-local.bundle HEAD
git submodule update --no-fetch integrations/RIMAPI
```

Skip `git clone` if the submodule repository already exists.

Changes:

- `game/state.session_id` identifies the loaded `Game` instance, independent of
  reused seeds/map IDs. Loading a save creates a new session; this intentionally
  starts a fresh controller plan. Reconnecting the controller to the same running
  game retains its state.
- Cached HTTP observations are scoped to that session, preventing old-game pawn
  data from surviving into a new game with reused numeric IDs.
- ThingDefs expose native material requirements, quantities and allowed materials.
  Blueprint batches validate material choices before placing any building.

Build `Source/RIMAPI/RIMAPI.csproj` with configuration `Release-1.6`, then install
`1.6/Assemblies/RIMAPI.dll` only while RimWorld is closed. The test installation
currently uses Workshop folder `3593423732`; Steam updates can overwrite it.
