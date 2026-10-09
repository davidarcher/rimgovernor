# Construction tiers

[Contracts](README.md) · Epic #2510. Tier is a per-target integer that native
remembers while a building is unfinished. This page covers the field and its
lifecycle (#2522), which builds carry which tier (#2524) and the native
delivery gate that reads it (#2523). Since #2525 every planner-placed build
states a tier; defense is Secure and promoted (#2527).

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
Absent means ungated vanilla behavior (a build the game or player placed), and
is distinct from tier 0.

## Which builds carry which tier

| Tier | Contents |
|---|---|
| 0 Survive | Initial shelter shell, sleeping spots and bunks, kitchen and cook station, essential sanitation, research bench |
| 1 Sustain | Private bedrooms, hospital, butchery, food stores (storage rooms and shelves), lab, cooler/freezer and its power, stonecutter |
| 2 Comfort | Dining and rec rooms, throne/worship rooms, suites, animal shelter and barn |
| 3 Produce | Other workshops and their bench input stores |
| 4 Expand | Graveyard, waste yard, outskirts, anything unlisted |
| 5 Secure | Defense ring, turrets and conduits, killbox, gates, mortars, IEDs, bait, wall support, armory and wardrobe (#2527) |

Power for something other than the cooler/freezer takes the tier of what it
serves; unlisted power is Expand.

Tier is chosen in pure policy (`policy/construction_tier.go`) and only passed
through by orchestration:

- Planned rooms: `policy.RoomTier(role)` over a table with one row per
  `PlannedRole`; a test fails a new role without a row. `commitBuilds` stamps
  every wall, door, floor and furniture build of a room with its role's tier.
  Storage rooms are Sustain (the role cannot tell a food store from a bench
  input store), armory and wardrobe are Secure.
- Loose buildings a Concern's planner places: `policy.PlannerTier(concern,
  phase)`; a concern the ladder does not list is Expand by decision. Defense
  concerns and the defense admit paths are Expand until #2527 assigns Secure,
  and the generic power planner is Expand (the proposal names no consumer).
  Sleeping bunks and stone-shell walls (Survive), bedroom upgrades, storage
  shelves and food-field infrastructure (Sustain) state their tier at the call.
  Rock steps and excavation preview the owning planner's buildings, so they take
  that planner's `PlannerTier`; the animal paddock marker is a `PlannedPen`
  piece placed through the room funnel (Comfort).
- Defense perimeter tiers are previewed and admitted in order every step, with
  no stock-funded admission (#2551); the tier gate decides who gets materials.
- A finishing-skill adoption of an existing site restates the tier native
  already reads on it (`policy.AdoptedTier`), so adopting never moves a build; a
  site with no tier is Expand. Placements nothing else lists (quest monuments,
  gene bank, mech charger, the HTTP building submission) are Expand.

## Required tier

`domain.NewBuildingAction(id, building, tier)` takes the tier as a required
argument, and a stored building action without one does not load, so an untiered
build cannot be constructed. `archgate.BuildingActionTiers` (run as
`TestBuildingActionCallersStateTheirTier`, with a negative fixture) walks every
non-test caller and fails one that omits the tier or passes anything other than
a `domain.Tier*` constant or a `policy.RoomTier`/`PlannerTier`/`AdoptedTier`
call. The only pass-through callers are `NewPlan` (canonicalizing an action that
already has its tier) and the store's row reader. There is no baseline: add the
tier, never an exemption.

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
`zone_payload` (`Tier` always; `Minimum` and `Target` when set; absent fields
omitted). Load rejects any other JSON shape, and a building row with no
payload or no `Tier`.

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

## Defense promotion

Defense builds are placed Secure (`policy.DefenseBuildTier`, #2527). They are
placed Survive, and already placed layout blueprints and frames are lifted to
Survive with the set-tier op (`RoundsDefenseLayoutPlanner.retier`, one
`defense-retier-*` method per set of sites), when
`policy.DefensePromoted(Facts.RaidPoints, Facts.DefenseCapacity)` holds: raid
points at least `DefensePromotionFloor` (300, the lowest armory threshold) and
above the observed capacity. Unknown raid points or capacity never promote. It
is re-evaluated each defense round; a site already at Survive is not selected,
so the op is idempotent. While a roamer is owned, the core ring is placed Comfort
so its materials arrive before other Secure builds (a promotion outranks that);
the sequence reorder of #2230 stays.

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

Admission is not metered by sequence: room builds (shell, floors, furniture,
across every room of a wing) are admitted in one round whatever the stock, and
a ring is never held for an open wave. A blueprint holds no materials until
delivered, so over-admission is harmless and this gate orders the delivery.
Logical prerequisites (kitchen before stove, dig before ring, stage gates)
stay in Go.
