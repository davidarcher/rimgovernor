# Native map resource overview

`GET /api/v1/map/resource-overview?map_id=0&center_x=125&center_z=125&nearby_radius=40`

Authored in RIMAPI's OpenAPI contract, with generated C# and Python DTOs and native response validation. The manager operation is `get_map_resource_overview`.

Before each review, the controller requests one survey centered on the observed colony focus. Strategy, daily planning and specialist contexts share it. No discovery call is needed to learn whether nearby resources exist. Activity records the survey under Observation. Missing support or a failed survey is reported as unavailable, never as zero resources.

The survey includes explored terrain/fertility and contiguous nearby terrain patches; wild and sown plants with current harvest estimates; wild/owned/hostile animals and potential meat; visible mineral blocks and base yields; loose supplies with allowed/forbidden quantities; native ground food crops with fertility, season, skill and base growth requirements; and Odyssey's native water-body/fishing data when available. Definitions and values come from the running game's registry and objects.

## Interpretation

- Nearby means straight-line distance, not a safe or reachable route. Inspect candidate cells/entities before orders. No blanket unforbid objective.
- Unexplored cells and hidden deposits are excluded. Pawn inventories and containers are excluded. Loose supplies include both stockpiles and ground stacks.
- Harvest, meat and mineral yields are estimates, not stored resources. Harvest estimates use native YieldNow with isolated, stable random rounding; the query does not advance gameplay RNG.
- Fertility-eligible area is potential ground, not validated planting space. Roofs, obstacles, pollution, snow, labor and prerequisites still matter. Same-terrain patches are not complete farm designs.
- Fishing uses native whole-water-body estimates, while water cell counts exclude fog. The optional frozen property is null if the installed game does not expose it. Other mods' independent fishing systems are not inferred.

## Cost and freshness

Terrain/fog is cached for up to 2,500 game ticks, invalidated by native terrain replacement/removal and fog changes. Other uncommon terrain mutation paths expire through the time limit. `refresh_terrain=true` forces a rebuild. Summaries keep up to four centers/radii per map. Plants, animals, minerals and loose supplies are read fresh through native indexes on each request. Timestamps distinguish terrain age from current observations.

The strategist projection bounds each category's rows and explicitly reports omitted groups. The full endpoint remains available for follow-up queries. It does not send every plant or terrain cell to the model. On the current test map: about 19,426 compact JSON characters for the full response and 17,377 for the bounded context; these are characters, not tokens. Further compaction should preserve the typed meanings and be measured against planning quality.

## Repeatable verification

With a loaded, paused colony and dashboard in Manual:

```powershell
.venv\Scripts\python.exe scripts\live_resources.py
.venv\Scripts\python.exe scripts\live_resource_strategy.py
```

The first validates the live DTO, stable snapshots, terrain cache hits, and resource accounting bounds. It writes `.rimbot/resources-live.json`. It checks consistency, not an independent census of every game entity or route safety. The second uses the real local model and production review path to submit a strategy without issuing orders; traces go under `.rimbot/resource-strategy/`. Neither proves autonomous execution of that strategy.

Live model check: one model call, 17.62 seconds, no game actions. It submitted a strategy referring to wild harvests, hare meat and existing corn, but remained generic and did not explicitly compare all food options. This verifies context delivery and structured submission, not reliable strategic judgment.
