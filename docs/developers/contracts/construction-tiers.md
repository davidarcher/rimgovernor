# Construction tiers

[Contracts](README.md) · Epic #2510. Tier is a per-target integer that native
remembers while a building is unfinished. This page covers the field and its
lifecycle (#2522), which builds carry which tier (#2524) and the native
delivery gate that reads it (#2523). The remaining admit paths (#2525) and
promotion (#2527) extend it.

## Ladder

| Tier | Name | `domain.ConstructionTier` |
|---|---|---|
| 0 | Survive | `TierSurvive` |
| 1 | Sustain | `TierSustain` |
| 2 | Comfort | `TierComfort` |
| 3 | Produce | `TierProduce` |
| 4 | Expand | `TierExpand` |
| 5 | Secure | `TierSecure` |

Tier orders construction only; the tech ladder is `TechTier`.
Absent means ungated vanilla behavior, and is distinct from tier 0.

## Which builds carry which tier

| Tier | Contents |
|---|---|
| 0 Survive | Initial shelter shell, sleeping spots and bunks, kitchen and cook station, essential sanitation, research bench |
| 1 Sustain | Private bedrooms, hospital, butchery, food stores (storage rooms and shelves), lab, cooler/freezer and its power, stonecutter |
| 2 Comfort | Dining and rec rooms, throne/worship rooms, suites, animal shelter and barn |
| 3 Produce | Other workshops and their bench input stores |
| 4 Expand | Graveyard, waste yard, outskirts, anything unlisted |
| 5 Secure | Defense ring, turrets, killbox, gates (#2527) |

Power for something other than the cooler/freezer takes the tier of what it
serves; unlisted power is Expand.

Tier is chosen in pure policy (`policy/construction_tier.go`) and only passed
through by orchestration:

- Planned rooms: `policy.RoomTier(role)` over a table with one row per
  `PlannedRole`; a test fails a new role without a row. `commitBuilds` stamps
  every wall, door, floor and furniture build of a room with its role's tier.
  Storage rooms are Sustain (the role cannot tell a food store from a bench
  input store), armory and wardrobe are Expand until #2527.
- Loose buildings a Concern's planner places: `policy.PlannerTier(concern,
  phase)`; unknown leaves the build untiered. Defense concerns stay untiered
  until #2527, and the generic power planner is Expand (the proposal names no
  consumer). Sleeping bunks (Survive), bedroom upgrades and storage shelves
  (Sustain) state their tier at the call.
- `RoundsBuildingPlanner.admitPreviews` requires `roundsAdmission.tiers`, one
  entry per preview, so each caller states its tier; a missing statement is a
  control error. Paths that do not use it (fields, excavation, rock steps,
  paddocks) are untiered until #2525.

## Place and set-tier

`BuildingIntent.tier` (`optional int32`, 0-5) rides the ordinary place op:
`Action.WithTier(tier, "")` places a tiered blueprint. Re-tiering reuses the
same intent: `Action.WithTier(tier, existingTargetID)` sets `existing_target_id`
and names the placement's geometry. Native resolves the blueprint or frame by
geometry, refuses a stale `existing_target_id` ("Construction setting target is
stale"), and applies the tier. The same value again is a no-op and a different
tier is accepted: tier is mutable. The finishing-skill floor on the same intent
stays write-once. Tier applies to any `Blueprint_Build` or `Frame`, quality
bearing or not; a completed building is refused.

The journal carries the pair in the building action's canonical
`zone_payload` (`Minimum`, `Tier`, `Target`; absent fields omitted). Load rejects
any other JSON shape.

## Native store

Tier shares the #2504 per-target store, `ConstructionSkillState`, scribed as
`rimgovernorConstructionTargets`: one `ConstructionSkillSetting` per exact thing
with `Minimum` and `Tier`, each `-1` for none (no minimum reads as allowed).
`Blueprint_Build.MakeSolidThing` rebinds the setting to the Frame. Saving prunes
settings whose target is destroyed or no longer a blueprint or frame, so
completion drops the tier; it is not carried onto the finished building.

## Readback

`ConstructionState.tier` (field 13) on blueprint and frame rows;
`observation.constructionSite` reads it into `policy.ConstructionSite.Tier`
(unknown when absent, a contract error outside 0-5).

Native case `wall/construction-skill` covers tier through conversion and
save/load, set-tier, the stale target refusal and the unchanged floor.

## Delivery gate

`ConstructionTierGate` (Harmony postfix on
`WorkGiver_ConstructDeliverResources.ResourceDeliverJobFor`, installed with the
other bridge guards) runs only under `Supervisor.IsActive`. It gates material
delivery, not labor: a frame needs every material before work starts, so
withholding delivery withholds work.

Rule, per resource: a tier-t site may receive material M only if no site of
strictly lower tier on the map still needs M. A site needs M when its
`TotalMaterialCost` lists M and `ThingCountNeeded(M) > 0` (stuff-based costs are
already concrete on the blueprint or frame). Equal tiers are not ordered against
each other. Untiered sites are never gated and never gate others. A material
nobody of lower tier wants flows freely, so unrelated shortages idle no one.

Mechanics: the vanilla job is `HaulToContainer` (`targetA` resource, `targetC`
the site it was found for, `targetB`/`targetQueueB` the sites it will deliver
to, including nearby needers). A blocked `targetC` returns no job with fail
reason "a lower construction tier still needs this material"; a blocked nearby
needer is dropped from the delivery list. The per-map list of tiered sites with
unmet materials is rebuilt once per game tick.

Deadlock guard: a lower-tier site blocks the delivering pawn only if that pawn
could itself deliver to it: same map, not forbidden, reachable (`Touch`,
`Danger.Deadly`) and the pawn's Construction level meets the def's prerequisite.
An unreachable tier-0 site cannot starve everything else. The comparison itself
is `ConstructionTierGatePolicy` (no Verse types), covered by probe
`native-construction-tier-gate`; the work-giver patch by native case
`wall/tier-gate`.
