# Durable policy and Auto control

[Subsystem contracts](README.md) · [Manual control](../architecture/control-loop.md#manual-control)

A native setting observed after Manual is current state, not permanent player
intent. Auto planners must reconsider it from fresh observations. Explicit
controller requests and startup policy remain deliberate inputs until replaced;
resuming Auto alone does not revoke them. Snapshot tokens, world identity and
native legality still guard every write.

## Stores and current ownership

| Store or setting | Writer and lifetime | Auto behavior |
| --- | --- | --- |
| `RoutinePolicy.ResourceTargets` and `StoneBlockTarget` | Startup configuration, not imported from native bills or Manual edits. | `EffectiveResourceTargets` combines configured floors with current stock and derived goal needs each review. They are acquisition floors, not spending prohibitions. |
| Pawn/animal allowed areas | Saved native pawn settings; colonist `WorkSettingsIntent`, animal husbandry `allowed_area`. | Recovery re-derives both from the fresh Auto census without requiring disaster history. A roof hazard retains/selects a roofed refuge; known absence clears restrictions for ordinary food/work access. Unknown safety never widens access. Native admission rechecks hazard, refuge reachability, current settings and world identity. Manual performs no correction. |
| Animal training and removal designations | Native saved settings/designations; routine husbandry selects training, tame and opted-in removal from a fresh census. | Training is selected from current availability/learned facts. Fresh Auto reviews reconcile standing release/slaughter flags with current herd floors, ceilings, removal opt-ins and food offers. Shared Hands cancels obsolete flags with an exact-animal husbandry intent; valid pending removals still suppress duplicate work. Upkeep and training resume from native readback. |
| Pawn food restrictions | Native saved food policies; `PolicyFacts.food` (holders and allowed foods) and `DefinitionCatalog.Foods` (each food's kind, game-computed once per load). | The Auto work planner gives each colonist, slave, prisoner and named tame animal the food policy labelled with its short name (#1541, #1543): `FoodPolicyIntent` writes the diet its traits and precepts allow (captives paste and raw food; animals kibble, hay and raw food, never meals or corpses), `PawnSettingsIntent.food_policy` assigns it. Native suitability, title, veneration, health and ordinary ingestion rules remain authoritative. Manual writes are refused. |
| Animal/feed and food policy configuration | Startup thresholds, herd floors/ceilings, removal opt-ins, food targets and storage fallback definitions. | Not inferred from Manual edits. Animal upkeep history stores deficit hysteresis and is recomputed from known current facts; unknown facts retain uncertainty, not a new prohibition. |
| Population decisions | Explicit submission tables, scoped to colony/load/map. | Separate controller intent; native observations do not silently create these requests. |

## Evidence boundaries

`store/work_preferences.go` is the only work-preference writer;
`buildingruntime/player_work.go` exposes it to the player API. Native priority
reads live in `observation/routine_work.go`; proposals and explicit overrides meet
in `policy/work_assignment.go` and `buildingruntime/routine_assignments.go`.

Resource target derivation lives in `policy/stone_blocks.go` and
`buildingruntime/routine_defense_layout.go`.

`store/routine_recovery.go` retains observed restrictions for reproducible proposal
readback and reconstructs them from each review's facts. It does not promote them
to work overrides. The current typed refuge writer in
`buildingruntime/routine_recovery.go` and `routine_areas.go` use explicit area
assignment/clear actions, not the legacy `RecoveryTools` lease. Corrections use
monotonic admission identities so repeated Manual restrictions remain correctable
after plan retirement or restart. A snapshot test over a recorded
takeover colony covers the correction; `recovery/area` checks
hazard protection and native CAS refusal.

`policy/animal_upkeep.go` and `policy/husbandry_upkeep.go` consume standing removal
flags. `Bridge/FoodSupplyFacts.cs` and `Bridge/Protocol/NativeColonyObservationTools.cs`
under the native source tree apply food filters. Refreshing those observations
cannot by itself remove a saved restriction. `NativeFoodPolicy` writes the
per-pawn diets `policy/food_policy.go` plans.

This inventory is a source audit, not gameplay validation. It changes no runtime
behavior. The takeover acceptance work remains in
[#479](https://github.com/davidarcher/rimgovernor/issues/479); new cases for these
store lifecycles belong with their implementation issues.
