# Player tool API audit — 2026-09-05

Audited against the locally installed RimWorld Assembly-CSharp implementation, using metadata/decompilation kept under ignored tmp/game-api. These are adapters, not a claim that RimWorld has a complete public remote-control API. All simulation state stays owned by RimWorld. The catalog is grouped by player system and alphabetized within groups.

## Execution paths

| Catalog tool | Native player control / implementation | Audit outcome and remaining limits |
| --- | --- | --- |
| architect_build | Designator_Build.CanDesignateCell / DesignateSingleCell; BuildCopyCommandUtility.FindAllowedDesignator | Replaced low-level blueprint-only helper. Native zero-work placement fixes sleeping spots. No fixed material names, room template or placement radius. Style/precept selection remains a gap. |
| areas_build_roof | Designator_AreaBuildRoof | Replaced raw BuildRoof assignment; native command clears NoRoof and supplies warnings. |
| orders_allow, orders_allow_area | Designator_Unforbid.CanDesignateThing / DesignateThing | Native Allow semantics; no hauling performed. IDs are revalidated. |
| orders_hunt | Designator_Hunt.CanDesignateThing / DesignateThing / ShowDesignationWarnings | Removed invented equipped-hunter prerequisite. Native warning and designation replacement behavior. |
| equipment_equip | FloatMenuOptionProvider_Equip.GetSingleOptionFor, invoked through a small subclass | Native checks and decorated menu action, including drafted pawns and automatic Allow. Persona/bond confirmation dialogs remain explicitly unsupported by automation. |
| zones_stockpile_designate, zones_growing_designate | Native zone designators: CanDesignateCell / DesignateMultiCell | Removed singleton stockpile and artificial proximity/8x8 command limits. Explicit optional zone ID selects extension target. Restores the player's selection afterward. Growing-zone plant choice uses Zone_Growing.SetPlantDefToGrow. |
| zones_remove_cells | Designator_ZoneDelete.DesignateMultiCell | Targets only cells belonging to the supplied zone ID; native split/empty-zone handling. |
| storage_configure | StorageSettings.CopyFrom / Priority; ThingFilter.SetAllow, SetDisallowAll, SetAllowAll and native ranges | Target stockpile or storage building, copy settings, exact definitions/categories/special filters, priority, quality and HP ranges. Validate on a detached candidate first. Fixed parent restrictions still apply. Storage group linking/unlinking and arbitrary mod-added custom storage UI are not yet adapted. |
| work_set_priority | Pawn_WorkSettings.SetPriority | Same setter as Work controls; incapabilities respected. Enabling simple/manual priority mode remains a player setting. |
| bills_add | RecipeDef.MakeNewBill / BillStack.AddBill | Separate explicit creation; observes native BillStack.MaxCount. Does not silently reuse a matching recipe. |
| bills_configure | Bill_Production fields used by BillRepeatModeUtility | Requires an existing bill ID. Native repeat modes; WorkerCounter.CanCountProducts validates targets. Ingredient filters, worker restrictions, output routing and other bill details remain gaps. |
| research_select | ResearchProjectDef.CanStartNow / ResearchManager.SetCurrentProject | Matches project selection path. Does not grant progress. DLC-specific research interactions are not comprehensively adapted. |

## Read adapters

| Catalog tool(s) | Source | Coverage / limits |
| --- | --- | --- |
| architect_catalog | DesignationCategoryDef.ResolvedAllowedDesignators | Native categories, labels and concrete control types; explicitly marks controls with no callable adapter. Dropdowns are reported, not expanded into a universal execution interface. |
| architect_buildables, architect_materials | Allowed building designators, ThingDef, StuffProperties.CanMake | Dynamic definition names/material compatibility and stocks. Building search returns 15 results; material query is paginated. No maintained wood/stone/steel whitelist. |
| buildings_list, construction_list | ListerBuildings and indexed blueprints/frames | Built/pending state and IDs. Building list paginated; construction list currently capped at 20. |
| rooms_list | RegionGrid.AllRooms / Room | Native room role, size, roof gaps and temperature. Enclosed visible rooms only; no inferred shelter scoring. |
| items_list, map_inspect | Haulable index, terrain/roof/zone/thing grids | Item filters and distance pagination; map observations bounded for response size. Distance does not claim path reachability. |
| pawns_list, pawns_inspect | MapPawns and pawn trackers | Needs, thoughts, direct relations, visible health, equipment, work and animal training. Paginated detail. Schedules, policies, complete social opinions, pens and husbandry remain gaps. |
| plants_sowable | PlantProperties / harvested ThingDef | Harvest product/yield, edibility, nutrition and requirements. Currently returns 20 definitions, so large mod packs need further pagination. |
| bills_list | Building_WorkTable / AllRecipes / BillStack | Real bill IDs, native repeat modes and target support. Currently limited to 12 tables / 15 recipes each. |
| research_list | ResearchProjectDef | Current availability/prerequisites; 25 unfinished projects. |
| zones_list | ZoneManager.AllZones | IDs, type, name, size and extents. |
| storage_inspect, storage_filter_options | IStoreSettingsParent / StorageSettings / ThingFilter / definition databases | Paginated effective allowed items, native settings and selectable definition names. |
| notifications_read | Native active alerts, LetterStack, Archive and live Messages | Read-only; does not dismiss/answer. Two specifically named private UI lists use cached reflection. Snapshot flags expose unavailable accessors. Notifications can span maps. |

## Manager-only tools and scheduling

- tools_search / tools_enable are protocol discovery, not simulated player actions. At most six recently enabled game schemas plus four manager/discovery schemas are attached; local calls/actions remain uncapped.
- manager_save_plan / manager_report_blocker store intent or expose a blocker; they are not game controls.
- A reasoned daily work brief sits between weekly/seasonal project review and zero-reasoning execution. Player direction invalidates outstanding decisions and replans. Saved tool references migrate known renamed tools; removed shelter actions remain unsupported until strategy regeneration.
- Notification lists are sampled locally once per second and deduplicated. Changes queue a review without a model polling tool. Changes during an execution request are checked before applying its orders, and cause reevaluation from fresh facts. Manual mode does not initiate model calls. This is application-side push, not a new engine event hook.
- Local request sizing now includes schemas, compacts to current game facts, and retries a context/output failure once. The model/server still owns the context and output limits; this does not guarantee that any arbitrary response fits.

## Removed prototype policy

Removed RoomConstruction, RoomLayout, ShelterGeometry, PlacementRange and their executable room/shelter/site tools. ShelterPlanner is now a save-compatibility shell only. No automatic world repair or demolition. A visible button explicitly replaces the old invalid sleeping-spot orders through native Cancel and Build actions.

## Verification

Production and isolated test-harness builds compile without warnings/errors. 122 remaining regression checks pass. The count is lower because tests of the removed template algorithms were deleted. Runtime assertions now cover native instant spot placement, repeat placement, definition-driven material discovery, stockpile filters/priority, and invalid-update atomicity. Those new in-game assertions have not yet been run; player testing is the next validation step. No claim of complete player API coverage or complete in-game verification.


## Drafting and direct orders

- `pawns_set_drafted` and `pawns_set_fire_at_will` invoke the pawn's native toggle gizmos, including disabled reasons. Explicit desired states make repeated calls idempotent.
- `pawns_orders` queries native DraftedMove, DraftedAttack and RescuePawn menu providers for one pawn and a visible target or cell. Disabled choices retain the game's explanation.
- `pawns_order` regenerates those choices and invokes the exact enabled choice. Native logic owns destination adjustment, range/path checks, rescue beds and job execution. An issued order does not claim completion.
- Provider context is restored even on failure. No hostile-count construction gate, automatic drafting, custom attack job or rescue-bed algorithm.
- Coverage is single-pawn movement, native right-click ranged/melee attacks, ordinary rescue and drafting/fire-at-will. Group formations, queue controls, capture, direct tending, abilities and special rescue variants remain gaps.
- Production checks pass; new in-game draft/move/invalid-choice assertions compile but have not been run. Attack damage and rescue completion still need runtime testing.


## Compact model transport

Game observations and fingerprints stay unchanged. StateTransfer creates separate execution and strategy views: no repeated tutorial paragraphs or duplicate allowed/forbidden totals; strategy omits placement coordinates and storage handles. Detailed queries remain available. Execution sends changed top-level fields after tool calls, with replacement semantics (including empty arrays); compaction sends a fresh full view. Tactical projects omit repeated purpose/dependency prose. Pawn inspection retains identity, capability, current orders, mood and requested detail/pagination while removing repeated classification metadata.

OrderMemory retains exact arguments and outcomes for recent actions/errors, replacing duplicate attempts. It is review-local and bounded; accepted means the tool succeeded, not that a pawn completed its job. Observed handles/restrictions remain separate. Full original tool results remain in the debug log. Discovery without a keyword lists all names; keyword results rank name matches first and use shorter descriptions. Enabled schemas retain their full contracts.

Verification: 162 regression/protocol assertions pass; both production and conditional game harness compile. Synthetic state fixture: 1163 characters originally, 782 execution, 685 strategy, 2 for an unchanged delta. These are character counts, not Qwen token measurements or proof of better decisions. `[RimBot Context]` logs actual provider input/output tokens plus transport sizes for subsequent in-game comparison. No runtime behavioral comparison or installation performed for this change.


## Full native pawn menu and work follow-up

Supersedes the earlier three-provider coverage limit: pawns_orders now calls FloatMenuMakerMap.GetOptions for the selected pawn at the target's cell. This is the game's full native menu, including work-giver prioritized construction/hauling and available equipment, wear, ingest, rescue, combat and DLC/mod providers. pawns_order regenerates that menu and invokes an exact enabled label; ambiguous labels are rejected. Target ID means clicking its cell, so nearby/co-located objects can also supply menu choices. Dialogs and secondary targeting are not automatically answered. Game god mode is excluded. Native provider/makingFor pointers are restored after calls.

Execution state includes native idle state, current jobs and pending construction workDone plus pawns whose current job targets the order. Targeting is not proof of productive work or completion; normal job state and subsequent work changes remain the evidence. Daily planning explicitly follows up on unfinished work with idle pawns and treats sleep/recreation as valid activities. Existing saved projects and daily work briefs remain the shared to-do list; no custom work scheduler or replacement job engine was added.

Production regression tests and game harness compile. Full-menu label/disabled-state equivalence assertions are in the harness but have not been run in-game; interactive dialogs and mod-provided options require testing.

## Persistent tracked work

The save now retains attempted orders, optional strategic project links, last results, and construction progress clocks. Today's plan shows tracked work. Construction is matched by map, location, definition and requested material: blueprint is Ordered, frame work is In progress, unchanged work for 7,500 game ticks is Stalled, an absent order is Missing, and an observed building is Complete. Stalled is a review signal, not a prohibition or claim about why work stopped. Reissuing the same order does not restart its clock. State transitions request follow-up only in Automate mode.

Other action types currently retain Issued/Rejected status; bills, research, storage and pawn-order completion need dedicated observed-outcome adapters. Orders predating this ledger are visible in normal game queries but are not automatically assigned to projects. Project links are optional model-supplied references. 182 regression/protocol checks pass, including persistence, map isolation, repeated-order timing, missing-versus-completed and progress recovery. The game harness compiles; runtime save/load and UI checks remain pending.


## Selection interaction

selection_inspect assembles an exact visible target, remaining construction costs, up to eight nearest matching supply stacks, up to six nearest colonists, and a visible 5x5 local map description. Counts and pagination show bounded coverage; distance is not reachability. Adding pawnId returns the native menu. Executable choices receive one-use, game/map-scoped handles, retained through conversation compaction and revalidated before use. Disabled choices have no handle. The model sees pawns_order only after an inspected menu has enabled choices, with actionId restricted in its schema to those choices; world actions clear this schema until another inspection. Cached handles are bounded to 128 and do not survive loading another game. Native dialogs still require player interaction.

Exact building definition or label matches suppress fuzzy alternatives; fuzzy results return at most eight definitions and three material samples each. Complete materials remain queried through architect_materials. Initial review schemas include selection_inspect and orders_allow_all, avoiding discovery for basic interaction.

Local Qwen replay uses simulated game results, not live game execution. Early iterations exposed invented handles and premature blocker reporting. The refined interface completed inspect -> allow supplies -> inspect -> returned action in four calls with zero invalid handles in the observed successful run (5,267 total tokens). This does not establish whole-colony reliability or validate runtime geometry/menu behavior. Re-run with tests\bin\Release\RimBot.Tests.exe --selection-live; the production game harness remains separate.

Replay follow-up: a repeated thinking-off run failed by saving a plan claiming the builder was assigned after Allow, without issuing the native work action. The same fixture with reasoning enabled completed in six calls (including two redundant enable calls), 8,391 tokens and zero invalid handles. These few stochastic runs establish neither a reliable pass rate nor a model-capacity limit. They do show that protocol-valid responses and concise context are insufficient evidence of gameplay success. Use --selection-thinking for the comparison; production reasoning settings were not changed automatically.


## Rotation removed

Supersedes earlier schema-window behavior: every execution request includes the full 39-tool catalog. tools_search and tools_enable are no longer offered or requested. Tools remain available across inspections, mutations and conversation compaction. Current native action IDs can refine the action argument schema, but never hide the command; runtime handle validation remains authoritative. Conversation compaction is based on message size, avoiding a permanent compact/requery cycle caused solely by the stable schema payload. No in-game behavioral success is claimed by these protocol checks.

Also corrected the legacy Equip adapter: it no longer subclasses a native menu provider, which RimWorld's provider discovery was registering as an extra duplicate Equip choice. Building lookup results now identify architect_build as the placement control and distinguish definitions from placed objects.


## Integrated manager UI

The main manager window now keeps conversation, direction entry and a Today/Strategy/Work side panel together. Replanning and project expansion stay inside the panel; old separate windows have no links from the main flow. A persistent status strip shows request phase, elapsed seconds and completed checks, accepted orders, failures and request counts. Breadcrumbs update from actual tool outcomes; waiting text is not a claim of access to streamed model thoughts. Follow-latest can be disabled for reading older messages. Detailed objective reasons remain tooltips.

Rejected API calls are excluded from both new tracked-work records and legacy displayed records; errors remain in activity and model error memory. UI rendering compiles, but visual sizing, scrolling and runtime behavior have not been verified in-game in this revision.


## Selective resource access

Removed Allow All recommendations from item results, empty-storage hints and summary state. Supply guidance now names selected item IDs and requires location/threat assessment; ground items do not need a stockpile, and reachability is not safety. The native global control remains callable but explicitly describes dangerous-area supplies and is not a default task. Selection inspection includes visible hostiles ordered by distance (bounded, not a safe-route classifier).

Strategic completion metrics must belong to the measured contract; the logged invented forbidden<=0 goal is rejected. Loading an invalid saved strategy discards its derived daily/next-action prose and requests fresh planning. No world items are automatically allowed, forbidden, moved or restored by this change. This does not guarantee that model risk assessment is reliable; native player controls remain available.
