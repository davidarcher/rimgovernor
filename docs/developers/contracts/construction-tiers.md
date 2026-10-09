# Construction tiers

[Contracts](README.md) · Epic #2510. Tier is a per-target integer that native
remembers while a building is unfinished. This page covers the field and its
lifecycle (#2522); nothing reads the tier yet. The delivery gate (#2523), room
tier assignment (#2524) and promotion (#2527) extend it.

## Ladder

| Tier | Name | `domain.ConstructionTier` |
|---|---|---|
| 0 | Survive | `TierSurvive` |
| 1 | Sustain | `TierSustain` |
| 2 | Comfort | `TierComfort` |
| 3 | Produce | `TierProduce` |
| 4 | Expand | `TierExpand` |
| 5 | Secure | `TierSecure` |

The names are fixed here; which buildings belong to which tier is a later child.
Absent means ungated vanilla behavior, and is distinct from tier 0.

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
