# Durable policy and Auto control

[Subsystem contracts](README.md) · [Manual control](../architecture/control-loop.md#manual-control)

A native setting observed after Manual is current state, not permanent player intent; Auto planners
reconsider it from fresh observations. Explicit brain requests and startup policy remain
deliberate inputs until replaced; resuming Auto alone does not revoke them. Snapshot tokens, world
identity and native legality still guard every write.

## Stores and current ownership

| Store or setting | Writer and lifetime | Auto behavior |
| --- | --- | --- |
| Derived resource needs | Not configuration: derived each review from construction demand, concern needs and the consumption ring's runway targets. | `ResourceConcernTargets` merges them with current stock each review. They are acquisition floors, not spending prohibitions. |
| Pawn/animal allowed areas | Saved native pawn settings; colonist `WorkSettingsIntent`, animal husbandry `allowed_area`. | Recovery re-derives both from the fresh Auto census without requiring disaster history. A roof hazard keeps/selects a roofed refuge; known absence clears restrictions for ordinary food/work access. Unknown safety never widens access. Native admission rechecks refuge reachability, current settings and world identity; it does not veto an unroofed area (policy assigns only the roofed refuge during a roof hazard). Manual performs no correction. |
| Animal training and removal designations | Native saved settings/designations; routine husbandry selects training, tame and opted-in removal from a fresh census. | Training is selected from current availability, learned and wanted facts; a wanted, unlearned skill waits on native handlers. Fresh Auto reviews reconcile standing release/slaughter flags with current herd floors, ceilings, removal opt-ins and food offers. Shared Hands cancels obsolete flags with an exact-animal husbandry intent; valid pending removals still suppress duplicate work. Upkeep and training resume from native readback. |
| Pawn food restrictions | Native saved food policies; `PolicyFacts.food` (holders and allowed foods) and `DefinitionCatalog.Foods` (each food's kind, game-computed once per load). | The Auto work planner gives each colonist, slave, prisoner and named tame animal the food policy labelled with its short name: `FoodPolicyIntent` writes the diet its traits and precepts allow (captives paste and raw food; animals kibble, hay and raw food, never meals or corpses), `PawnSettingsIntent.food_policy` assigns it. Native suitability, title, veneration, health and ordinary ingestion rules stay authoritative. Manual writes are refused. |
| Animal/feed and food policy configuration | Startup thresholds, herd floors/ceilings, removal opt-ins, food targets and storage fallback definitions. | Not inferred from Manual edits. Animal upkeep history stores deficit hysteresis and is recomputed from known current facts; unknown facts retain uncertainty, not a new prohibition. |
| Defense layout record | Journal singleton (`family/defense_layout`, `DefenseLayout.cs`), written by the `layout/defense` Need's step (`RoundsDefenseLayout.cs`, `RoundsDefensePerimeter.cs`); dropped with the world. Per-tier attempts restart for unbuilt tiers in another load; an older save's `Standard` and `Episode` fields are ignored (#3314). Whether the layout was verified complete, and when the siting was last probed, are not stored: the Rounder keeps them in memory per world (#3008). | The layout plan owns the geometry (#2923, epic #2855): the record names the plan's killbox node and a digest of its footprint, and a plan that moves the killbox clears the name so the layout is proposed afresh. Entry, direction and width are read from the plan at use, and so are the firing line and the trap lane: the plan's DefFiring and DefLane tier nodes hold them, the record does not (#3583). Perimeter tiers cut from plan nodes (ring and gates, fence, pumps) store the node ids and their state, never buildings; the planner resolves the buildings from the plan after every load. The perimeter tiers are its ownership: every review recuts them against the plan and the census (#3585), and what they own that the cut no longer wants and the census shows standing becomes removal tiers, which store their buildings and name the nodes they came from. A removal's order is derived each review, not stored: ahead of the cut's sections where they build on the cell, behind them elsewhere, a bridge lifted after what stands on it; the bridge an old wall stands on is read from the census terrain. A ring replaced while the record held it stands beside the old one under revision-suffixed ids (`ReplacesSites`) and goes through the same recut: the old nodes stay in the plan until no tier holds them, and the old wall comes down only after the new ring is built (#2924). Tiers derived from a live census (hallway firing line, funnel, trap corridor, IEDs, turrets, mortars, prison turrets, lights) and the pocket and bait tiers (until #2925) keep stored buildings; the reserved cells of the killbox tiers are the plan's tier nodes, which also pin a started tier (#3584). |

## Code map

- Native priority reads live in `RoundsWork.cs`; proposals
  and explicit overrides meet in `WorkAssignment.cs` and `RoundsAssignments.cs`.
- Resource target derivation: `RoundsDefenseLayout.cs`.
- `RoundsRecovery.cs` retains observed restrictions for reproducible proposal readback and
  reconstructs them from each review's facts; it does not promote them to work overrides. The typed
  refuge writer (`RoundsRecovery.cs`, `RoundsAreas.cs`) uses explicit area
  assignment/clear actions. Corrections use monotonic admission identities so repeated Manual
  restrictions stay correctable after plan retirement or restart. A snapshot test over a recorded
  takeover colony covers the correction; `recovery/area` checks hazard protection and native CAS
  refusal.
- `AnimalUpkeep.cs` and `HusbandryUpkeep.cs` consume standing removal flags.
  `Bridge/FoodSupplyFacts.cs` and `Bridge/Protocol/NativeColonyObservationTools.cs` (native source
  tree) apply food filters; refreshing those observations cannot by itself remove a saved restriction.
  `NativeFoodPolicy` writes the per-pawn diets `FoodPolicy.cs` plans.

This inventory is a source audit, not gameplay validation. Takeover acceptance is open in
[#479](https://github.com/davidarcher/rimgovernor/issues/479); new cases for these store lifecycles
belong with their implementation issues.
