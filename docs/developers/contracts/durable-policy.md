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
| `work_preferences`, `work_preference_requests` | `Store.SetWorkPreferences`; explicit API request, scoped to a plan/world, revision checked; an empty replacement clears overrides. | `RoutineWorkPlanner` reads these separately from current native priorities. `PlanWork` recomputes priorities from the roster and demand; observed priority zero is not copied into an override. Explicit overrides, including zero, remain authoritative outside a temporary disease rest hold; the hold masks them until immunity without rewriting them. |
| `RoutinePolicy.ResourceTargets` and `StoneBlockTarget` | Startup configuration, not imported from native bills or Manual edits. | `EffectiveResourceTargets` combines configured floors with current stock and derived goal needs each review. They are acquisition floors, not spending prohibitions. |
| `resource_policies`, `resource_policy_submissions` | Explicit API/chat directives keyed by colony/load/map/resource; normal/defense-only/stop and reserve persist until replaced. | Explicit directives override both reserve and spending for their resource, including zero/normal; startup reserves/stops supply defaults for other resources. Submission and routine reconciliation use this same merge. Auto resume retains current directives. |
| Native `ProductionPolicyState.Floors` / `Stopped` | Saved in the game; full map replacement through `SetProductionPolicy`. Commitments are transient and not serialized by this component. | Fresh native floors/stops are observed state, never imported as intent. Auto replaces drift with the merged desired policy, including an empty replacement after config removal or save/load. Completed writes do not suppress later identical repairs; open work still prevents duplicate plans. Snapshot, world and authority checks guard dispatch; commitments/drills are preserved from a fresh read. |
| Pawn/animal allowed areas | Saved native pawn settings; colonist `PatchPawn`, animal husbandry `allowed_area`. | Recovery re-derives both from the fresh Auto census without requiring disaster history. A roof hazard retains/selects a roofed refuge; known absence clears restrictions for ordinary food/work access. Unknown safety never widens access. Native admission rechecks hazard, refuge reachability, current settings and world identity. Manual performs no correction. |
| Animal training and removal designations | Native saved settings/designations; routine husbandry selects training, tame and opted-in removal from a fresh census. | Training is selected from current availability/learned facts. Fresh Auto reviews reconcile standing release/slaughter flags with current herd floors, ceilings, removal opt-ins and food offers. Shared Hands cancels obsolete flags with exact animal/census guards; valid pending removals still suppress duplicate work. Upkeep and training resume from native readback. |
| Pawn food restrictions | Native saved diet assignment/filter; typed `PawnSettings.food_restriction` separates allowed definitions from native eligibility. | The Auto work-settings planner restores missing eligible definitions through shared plan/Hands and `PatchPawn.food_allow`. Exact CAS covers policy identity and the complete filter. The writer assigns an equivalent or copied policy, preserving special filters/ranges and leaving shared policies unchanged. Native suitability, title, veneration, health and ordinary ingestion rules remain authoritative. Manual writes are refused. |
| Animal/feed and food policy configuration | Startup thresholds, herd floors/ceilings, removal opt-ins, food targets and storage fallback definitions. | Not inferred from Manual edits. Animal upkeep history stores deficit hysteresis and is recomputed from known current facts; unknown facts retain uncertainty, not a new prohibition. |
| Population/expedition policies and population decisions | Explicit submission tables, scoped to colony/load/map. | Separate controller intent; native observations do not silently create these requests. |

## Evidence boundaries

`store/work_preferences.go` is the only work-preference writer;
`buildingruntime/player_work.go` exposes it to the player API. Native priority
reads live in `observation/routine_work.go`; proposals and explicit overrides meet
in `policy/work_assignment.go` and `buildingruntime/routine_assignments.go`.

Resource target derivation lives in `policy/stone_blocks.go` and
`buildingruntime/routine_defense_layout.go`. Explicit resource directives live in
`store/resource_policy.go`. `buildingruntime/routine_production_policy.go` uses
startup reserves/stops overridden by current world-scoped directives, while `policy/routine.go` determines whether its goal is
active. Native save serialization is
`integrations/rimgovernor-native/src/Runtime/Persistence/ProductionPolicyState.cs`.

`store/routine_recovery.go` retains observed restrictions for reproducible proposal
readback and reconstructs them from each review's facts. It does not promote them
to work overrides. The current typed refuge writer in
`buildingruntime/routine_recovery.go` and `routine_areas.go` use explicit area
assignment/clear actions, not the legacy `RecoveryTools` lease. Corrections use
monotonic admission identities so repeated Manual restrictions remain correctable
after plan retirement or restart. `takeover/allowed-areas` covers repeated
restrictions, save-load and actual colonist/animal feeding; `recovery/area` checks
hazard protection and native CAS refusal.

`policy/animal_upkeep.go` and `policy/husbandry_upkeep.go` consume standing removal
flags. `Bridge/FoodSupplyFacts.cs` and `Bridge/Protocol/NativeColonyObservationTools.cs`
under the native source tree apply food filters. Refreshing those observations
cannot by itself remove a saved restriction. `NativeFoodPolicy` supplies the
guarded food-policy expansion; `takeover/food-policy` checks repeated Manual
edits, stale settings, actual eating/nutrition recovery and native save/load.

This inventory is a source audit, not gameplay validation. It changes no runtime
behavior. The takeover acceptance work remains in
[#479](https://github.com/davidarcher/rimgovernor/issues/479); new cases for these
store lifecycles belong with their implementation issues.
