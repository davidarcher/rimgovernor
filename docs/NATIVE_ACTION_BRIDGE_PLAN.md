# Native action bridge: implementation plan

Status: audited proposal, queued after the hierarchical manager integration. No runtime migration is claimed by this document. Inspected 2026-09-05 against the locally installed RimWorld 1.6 Assembly-CSharp.dll and current source, including the pending spatial preview/line construction changes.

User clarification: backward compatibility for the AI integration is not required. Prefer the direct new interface and discard obsolete AI data where necessary; preserve native RimWorld colony state.

## Boundary

Keep four stable model tools: `get_colony_summary`, `query`, `actions`, `execute`. Their schemas describe curated DTOs, never arbitrary C# members. `actions(context)` returns native choices and applicable semantic operations with argument schemas. `execute(action_id,args)` receives only an opaque runtime handle. Specialist proposals reference these descriptors; only the Administrator-approved executor invokes them.

This is not a universal registry supplied by RimWorld. RimWorld provides several discovery mechanisms with different context and invocation contracts. The bridge joins those mechanisms and admits their limitations instead of recreating each game button as a tool.

## Current implementation and duplication

- `ToolCatalog` serializes 40 manually registered tool schemas. `ToolSession` always supplies that full list and temporarily narrows the native action ID enum. `ToolNames` currently retains aliases for old saved plans; remove those aliases with the new interface.
- `ColonyTools.Execute` and `ColonyDevelopment.Execute` dispatch names into adapters. `IsAction` separately lists mutating names. `ColonyManager.Request` currently executes responses on the main-thread callback, records activity and successful task-ledger actions, and refreshes state. The hierarchy must replace the direct model-to-executor path before migration.
- `PawnDirectOrders.Inspect` already uses the complete `FloatMenuMakerMap.GetOptions` menu. This is a useful existing vertical slice, not a missing right-click implementation. Its registry stores arguments, map, and display label; execution regenerates the menu and matches that label. IDs are one-shot, bounded to 128, and cleared on game replacement. There is no time expiry or structured identity, and same-label replacement is still possible.
- `PlayerOrders.Equip` repeats provider selection and some eligibility checks already represented by that full menu. `PawnDirectOrders.Toggle` handpicks Draft and Fire at will from native gizmos. These are the first duplication to remove.
- `PlayerConstruction` already validates and designates through `Designator_Build`, including instant placement. Keep this engine-backed adapter. Do not replace it with hand-built blueprints or a room recipe.
- `ArchitectCatalog` discovers native categories/designators but maps only seven subclasses to bespoke adapter names. Listing an unknown designator currently does not make it executable.
- `PlayerOrders.Zone` scopes/restores native selection. Storage uses native `StorageSettings`/`ThingFilter`; bills use native recipe/bill APIs. These are useful semantic adapters for editor-style UI, not candidates for generic button clicking.
- State is already curated across `ColonyObserver`, `PawnQueries`, `BuildingQueries`, `SelectionInspection`, `StorageTools`, `SpatialView`, and notification adapters. `StateTransfer` compacts these observations; it does not justify exposing the game object graph.
- `tests/Program.cs` checks schemas, request serialization and protocol fixtures. `tests/GameSmoke.cs` contains conditional native menu/draft checks; these are not a substitute for running the proposed new registry tests. `tests/GameBenchmark.cs` is a real-game bed construction fixture.

## Complete current catalog classification

Every current tool appears below. Some existing semantic wrappers become implementation details behind a single dynamic descriptor rather than separate model tools.

| Classification | Current tools | Destination |
|---|---|---|
| Native discovery candidates | `equipment_equip`, `pawns_set_drafted`, `pawns_set_fire_at_will`, `orders_hunt`, `orders_allow_all` | Native menu, gizmo or designator option handles |
| Native discovery/execution bridge | `pawns_orders`, `pawns_order` | `actions` / `execute`; replace label matching |
| Semantic geometry | `architect_build`, `areas_build_roof`, `orders_allow`, `orders_allow_area`, `zones_growing_designate`, `zones_stockpile_designate`, `zones_remove_cells` | Discovered shape/selection operations, native validation per target |
| Semantic configuration | `storage_configure`, `work_set_priority`, `bills_add`, `bills_configure`, `research_select` | Discovered typed configuration operations backed by native state APIs |
| Read-only query adapters | `architect_buildables`, `architect_catalog`, `architect_materials`, `construction_list`, `storage_filter_options`, `storage_inspect`, `zones_list`, `selection_inspect`, `pawns_inspect`, `pawns_list`, `buildings_list`, `items_list`, `map_inspect`, `rooms_list`, `bills_list`, `plants_sowable`, `research_list`, `notifications_read` | `query` selectors and field groups |
| Read-only semantic geometry | `architect_preview` | `query` construction-preview selector |
| Internal orchestration | `manager_report_blocker`, `manager_save_plan` | Typed proposal/decision fields; not world capabilities |

Future schedule/policy configuration belongs in the small semantic layer only after verifying its native model and UI validation. This audit does not claim those operations exist today.

## Verified native seams

Verified by current code and decompilation of the installed assembly:

- `FloatMenuMakerMap.GetOptions(List<Pawn>, Vector3, out FloatMenuContext)` returns the normal aggregate menu. Its `Init` discovers `FloatMenuOptionProvider.AllSubclassesNonAbstract()` and instantiates them. Use that aggregator; do not duplicate its provider registry. It preserves native eligibility and provider extensions, though this does not guarantee every mod's UI is representable.
- `FloatMenuOption` exposes `Action action`, `tutorTag`, `revalidateClickTarget`, `targetsDespawned`, `revalidateWorldClickTarget`, `isGoto`, and display fields. `Disabled` means the action is null. There is no universal structured category or separate disabled-reason field; retain the full disabled label without pretending its words are a reliable parser protocol.
- `Command_Action` exposes `action`; `Command_Toggle` exposes `isActive` and `toggleAction`. The existing code checks `Command.Disabled` and `disabledReason`. Not every `Gizmo` is a callable command; not every command is an immediate world action.
- `ThingWithComps.GetGizmos()` includes base gizmos and each component's `CompGetGizmosExtra()`. Enumerating components separately would duplicate commands.
- `DesignationCategoryDef.ResolvedAllowedDesignators` is already enumerated. `Designator` exposes `CanDesignateThing`, `DesignateThing`, `CanDesignateCell`, `DesignateSingleCell`, `DesignateMultiCell`, `RightClickFloatMenuOptions`, and `Finalize(bool)`. Base designators consult `Find.CurrentMap`. Per-subclass warnings/finalization must be audited; a base-method signature alone does not prove safe generic invocation.
- Native commands can open a dialog, start targeting, or depend on selected objects. Do not invoke unknown `ProcessInput` methods or simulate mouse input. Surface unsupported interaction modes honestly. A callable delegate does not prove that its effect completes without UI.

## Concrete classes and contracts

Proposed interfaces below are bridge designs, not claimed game API signatures.

- `ActionContext`: map ID, selected entity IDs, optional actor ID and target (thing or cell), requested action family. IDs have namespaces; do not confuse zones, bills and things.
- `ActionDescriptor`: opaque ID, label, enabled, disabled reason when native metadata supplies it, interaction kind, argument schema, actor/target IDs, optional category and ownership/effect metadata. Unknown category stays unknown.
- `INativeActionSource.Discover(ActionContext)`: main-thread-only source returning internal native candidates. Implement `FloatMenuActionSource`, `GizmoActionSource`, then a deliberately audited `DesignatorActionSource`.
- `NativeActionRegistry`: holds delegate/candidate, native identity evidence, original context, game/map/session generation, expiry, and revalidation adapter. Never serialize delegates or persist handles into saves.
- `NativeContextScope`: restores temporary selection and native menu globals in `finally`. Reject the wrong current map rather than silently switching the player's map. Avoid keeping selection changed while awaiting the model.
- `NativeActionBridge.Discover(context)` and `Invoke(approvedAction)`: resolve entities and revalidate on the main thread immediately before invoking. No model callback can call `Invoke` directly.
- `SemanticActionSource`: registers a small set of typed geometry and configuration capabilities. Uses existing `PlayerConstruction`, storage, work and bill adapters. Same descriptor/approval/execution result shape as native actions.
- `QueryAdapter.Query(selector)`: curated entity/filter/field-group/pagination contract over existing readers. Validate allowlisted selectors and fields, bound result bytes, return total/next cursor. Do not evaluate arbitrary reflection expressions.
- `ActionExecutionResult`: status (`issued`, `applied`, `stale`, `disabled`, `unsupported_interaction`, `failed`, `partial`), native reasons, affected entities/sites, and observed changes. Issued jobs are not completed work.

### Registry identity and stale handling

Opaque IDs alone do not fix the current label-based revalidation. The new entry retains the actual discovered candidate and original context. Revalidation regenerates candidates and compares available structured evidence (target, tutor tag, invocation kind, native adapter identity). Never select a replacement merely because its label matches.

Native FloatMenuOption has no universal stable identity or legality callback. For adapters without sufficient identity evidence, use a conservative context revision/short lease and reject ambiguous regeneration; do not claim arbitrary mod delegates can be safely matched. Labels may be additional evidence but never the sole identity. Audit whether provider provenance can be captured without bypassing the aggregate menu before implementing a provenance hook.

Consume an ID before invocation; exceptions cannot silently retry the same delegate. Invalidate on game/map changes, Manual mode, cycle cancellation, actor/target deletion, expiry, and relevant completed mutations. Expired entries must release captured references. A batch revalidates each remaining entry after preceding mutations and reports exact partial outcomes. Never retarget a destroyed blueprint ID to a frame automatically.

Do not invalidate every handle at every tick: inference latency would make all actions expire. Revision scope should cover actor/target/context dependencies; native revalidation remains authoritative. The first slice should prefer explicit stale errors over optimistic matching.

## Hierarchical manager integration

Use `ProposedAction {descriptorId,args,expectedEffects,sourceManager}` as data. Discovery and queries do not mutate; proposal classes contain no delegate or executor reference. Administrator approval produces `ApprovedAction` records, and only the colony loop owns `IApprovedActionExecutor`.

Ownership checks must consult server-side capability metadata, never the manager's claimed category or the action label. Workforce alone authors global work-priority and schedule changes after accepted labor requests. Infrastructure can propose construction/bills but cannot smuggle reassignment through a native gizmo. Security owns active combat; a standing insect in a cave is not automatically an active emergency. Survival requests labor; it does not set global priorities.

Unknown mod commands may lack trustworthy effect/domain metadata. Present them as unclassified and require Administrator arbitration; do not expose them as unrestricted specialist execution. Unknown interaction mechanisms remain unsupported until an adapter exists. This is a real enforcement limitation, not something prompt instructions solve. Keep action ownership separate from RimWorld legality: the former is bridge policy, the latter stays native.

Do not persist ephemeral action IDs as commitments. Commitments retain intent, resources and durable entity/site references. Rediscover capabilities before execution and return changed availability to arbitration. Refresh once immediately before executing accepted actions so sequential specialist inference does not invalidate an entire cycle's early proposals without explanation.

## Geometry and call count

Construction should accept a collection of placements/shapes in one accepted action: cells, inclusive lines, rectangles/perimeters, rotations and discovered stuff definitions. The model chooses the layout. Preview returns native per-placement failures and compact spatial feedback; execution reports each applied/skipped/failed site. No hardcoded shelter or material recipe.

Do not turn a wall perimeter into dozens of LLM round trips. CPU validation bounds, model response call count, review request budget, and total supported geometry are different limits. Process bounded chunks on the game thread while retaining the accepted operation; do not require a new model decision for each cell. If yielding is necessary, preserve continuation state and display remaining work. Native execution is not transactional: report partial completion, never roll back by deleting player work or claim all-or-nothing.

## Staged implementation and acceptance

1. After hierarchy integration, extract the existing native menu path into registry/source classes. Expose the new `actions`/`execute` interface directly and remove displaced tool aliases. Reuse current query implementations behind the new query boundary; no legacy API compatibility layer is required.
2. First proof: retire the separate `equipment_equip` execution path in favor of discovered Equip/Wear. Second and third: Draft and Fire at will through gizmo descriptors, with desired-state semantics for toggles. Discard obsolete AI plans and references instead of translating old names; no new model tools per button.
3. Run native tests for a real equip/order and toggle, plus a test-only mod component that contributes a simple immediate command. Compare disabled choices with the native menu. Report unsupported targeting/dialog actions rather than invoking them blindly.
4. Add generic query wrapper and semantic shape/configuration descriptors. Replace the old model-facing interface directly. Derive mutation classification from descriptors rather than another string list.
5. Migrate Hunt and architect context options only after testing native warnings/finalization. Keep Allow-all as a discoverable deliberate choice; never make zero forbidden items an objective.
6. Remove retired schemas and aliases after new-interface protocol and gameplay checks. Obsolete AI plans/history may be discarded. Do not rewrite unrelated RimWorld save contents, entities, or player work.

Required automated registry tests: disabled action rejected; unknown/consumed/expired ID rejected; same label with different target rejected; state change disables action; deleted actor/target rejected; changed map/game rejected; thrown delegate returns failure and releases handle; cleanup bounds retained references; adapter with insufficient identity refuses ambiguous action; unclassified actions cannot bypass specialist ownership. Fake-source tests establish protocol behavior; conditional game tests establish native behavior.

Required gameplay regressions: weapon equip, drafted move, prioritized work, stockpile filters, instant sleeping spot, line construction with partial native rejection, and a completed enclosed/roofed bed area without duplicate beds. Validate save/load invalidates handles and that unsupported old AI data is cleanly discarded without changing native colony state. Record calls per meaningful accepted operation; compilation alone does not establish colony competence.

No fine-tuning, new server, UI automation, unrestricted reflection, or wholesale engine reimplementation is needed.

