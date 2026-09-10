# Verify equipment upkeep in Docker

[Documentation](../README.md) · [Equipment contracts](../reference/equipment-upkeep.md)

Use an isolated worktree and the private licensed inputs described in
[native Docker acceptance](docker-native.md). Build the companion against the
Linux references with `-p:GearFixture=true`; this adds disposable `test/gear_fixture`
setup. Default builds exclude it, and the controller gateway never exposes test
tools to model execution. Stage that DLL only in the run's private mod tree.

Run a fresh `rimbot.container_worker` with the task worker image and command:

```text
-- python /app/scripts/gear_upkeep_acceptance.py --source-root /worker/run --output /worker/acceptance --production
```

The worker needs private game, mods, profile and GABS mounts as described in the
native Docker guide. The output must not exist. `--seconds` defaults to 180 for
each dressing/equipping wait; production receives four times that wall-time budget.
Use `--production-only` to select just the missing-stock procurement/crafting/wearing
case when the loadout matrix has already been accepted on the same implementation.

Require process exit 0 and `acceptance/result.json` with `outcome: passed`.
The scenario retains native initial/final gear, negative admission results, shared
Hands progress, weapon replacement evidence and optional production/material
consumption evidence. Keep worker logs, private inputs' hashes and failed runs.

The fixture prepares damaged garments, weapons, clothing research and a workshop
in a disposable colony. Subsequent dressing, equipping and crafting use ordinary pawn work.
These assertions establish bounded scripted outcomes, not model interpretation,
arbitrary mod compatibility, optimal combat loadouts or long-term seasonal survival.
