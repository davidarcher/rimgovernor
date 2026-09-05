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
