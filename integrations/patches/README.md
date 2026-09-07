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

- Resource stacks expose native food types and stuff categories for strategic nutrition/material aggregation.

- Native undrafted hostility response (Ignore / Fight / Flee) is exposed through the player status endpoint and documented in OpenAPI.

- `game/state.session_id` identifies the loaded `Game` instance, independent of
  reused seeds/map IDs. Loading a save creates a new session; this intentionally
  starts a fresh controller plan. Reconnecting the controller to the same running
  game retains its state.
- Cached HTTP observations are scoped to that session, preventing old-game pawn
  data from surviving into a new game with reused numeric IDs.
- ThingDefs expose native material requirements, quantities and allowed materials.
  Blueprint batches validate material choices before placing any building.
- Food summaries count non-perishable meals and populate meal/raw-food counts;
  forbidden and unforbidden nutrition are separate observations, not assumptions
  about reachability or safety.
- Construction v2 uses authored OpenAPI contracts, generated C#/Python models,
  native placement validation, and blueprint/frame/building identity checks.
- The complete built-in HTTP surface is documented in OpenAPI, bundled into the
  native DLL, and served at `/api/openapi.json`.

- Nearby room sampling and local terrain queries expose observed construction sites.
- Construction revisions reject batches based on state superseded by player edits.

- Native Work tab manual-priority settings have schema-generated DTOs and use
  the same pawn notifications as the game UI.

Build `Source/RIMAPI/RimApi.csproj` with configuration `Release-1.6`, then install
`1.6/Assemblies/RIMAPI.dll` only while RimWorld is closed. The test installation
currently uses Workshop folder `3593423732`; Steam updates can overwrite it.

Construction eligibility: map-scoped definition discovery reports research and
spawned-colonist capability, skills and native ideology restrictions. New v2
placements recheck these restrictions; existing structures remain inspectable.
Temporary health, priorities and reachability are separate work observations.
Run scripts/check_build_eligibility.py --restricted SlabBed --available Bed
against a suitable paused test colony for the read-only regression.
