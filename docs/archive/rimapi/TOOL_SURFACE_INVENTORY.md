# Source inventory — 2026-09-07

Static inventory; see TOOL_SURFACE_AUDIT.md for behavior, limitations and recommendations.

## RimMolt: 113 named definitions, excluding aliases

DLC-gated registration accounts for 13 definitions; the remaining 100 are registered independently of those gates. Individual tools still have game/settings requirements.

- **ActionTools.cs**: `list_main_buttons`, `list_architect`, `do_thing_action`, `designate`, `build`, `set_work_priority`, `wait_for_event`, `set_speed`, `order_pawn`, `draft`, `set_research`
- **AnimalTools.cs**: `list_wildlife`, `list_animals`, `manage_animal`
- **AnomalyTools.cs**: `get_anomaly`, `entity_codex`, `list_study_targets`, `set_study` (Anomaly)
- **AreaTools.cs**: `manage_area`
- **AssignTools.cs**: `assign_building`
- **BillTools.cs**: `list_recipes`, `list_bills`, `add_bill`, `set_bill`, `delete_bill`
- **EntityTools.cs**: `list_things`, `get_area`, `list_unmanaged_items`, `list_fires`, `get_conditions`, `get_room`, `inspect_thing`, `get_info_card`
- **GameSetupTools.cs**: `game_setup_status`, `main_menu`, `save_game`, `load_game`, `return_to_title`, `select_scenario`, `select_storyteller`, `create_world`, `select_starting_site`, `choose_ideoligion`, `edit_ideoligion`, `edit_starting_pawn`, `start_game`, `reform_ideoligion`
- **GearTools.cs**: `manage_gear`
- **GeneTools.cs**: `list_genes`, `create_xenogerm`, `implant_xenogerm` (Biotech)
- **HealthTools.cs**: `set_medical_care`, `list_surgeries`, `add_surgery`
- **HelpTools.cs**: `help`
- **IdeoRoleTools.cs**: `set_ideo_role`
- **InspectPaneTools.cs**: `get_inspect_pane`
- **MechTools.cs**: `list_mechs`, `set_mech_control` (Biotech)
- **PolicyTools.cs**: `list_policies`, `set_schedule`, `set_outfit`, `set_drug_policy`, `manage_apparel_policy`, `manage_food_policy`, `manage_drug_policy`, `set_food_policy`, `set_hostility_response`, `set_allowed_area`
- **PowerTools.cs**: `list_power_grids`
- **PrisonerTools.cs**: `manage_prisoner`
- **QuestTools.cs**: `get_quest`, `quest_action`
- **RenameTools.cs**: `rename_pawn`
- **RimMoltTools.cs**: `get_status`, `list_colonists`, `get_pawn`, `get_resources`, `get_resource_readout`, `get_research`, `learning_helper`, `get_map`, `get_alerts`, `read_letter`, `get_world`
- **RoomTools.cs**: `room_graph`
- **RoyaltyTools.cs**: `get_royalty`, `list_titles`, `manage_permits`, `use_permit` (Royalty)
- **SayTools.cs**: `say`
- **ScreenshotTools.cs**: `screenshot`
- **TradeTools.cs**: `list_trade`, `set_trade`, `trade_action`
- **WindowTools.cs**: `list_windows`, `get_window_ui`, `window_action`
- **WorldTools.cs**: `list_world_objects`, `get_world_tile`, `caravan_action`, `world_target`, `find_world_tiles`, `form_caravan`, `world_object_action`
- **YoutubeTools.cs**: `get_live_chat`
- **ZoneTools.cs**: `list_zones`, `select_zone`, `rename_zone`, `delete_zone`, `set_growing_zone`, `set_stockpile_priority`, `set_stockpile_filter`

## AutoRim: 24 groups, 116 exposed actions

All 116 facade actions have matching backend command classes at the inspected commit. Three backend-only commands bring the backend total to 119.

- **rimworld_colony**: `snapshot`, `alerts`, `letters`, `resources`, `power`
- **rimworld_pawns**: `list`, `detail`, `rename`, `set_medical_care`, `set_area`, `set_hostility_response`, `list_equippable`, `equip`, `wear`, `unequip`
- **rimworld_work**: `list_types`, `get_priorities`, `set_priority`, `set_bulk`, `clear`
- **rimworld_jobs**: `current`, `draft`, `move_to`, `attack`, `prioritize`, `stop`
- **rimworld_designate**: `list`, `hunt`, `mine`, `mine_vein`, `chop`, `cut`, `harvest`, `tame`, `haul`, `forbid`, `unforbid`, `claim`, `smooth`, `cancel`, `slaughter`, `deconstruct`, `strip`, `release_animal`, `uninstall`
- **rimworld_build**: `place`, `place_line`, `check`, `list_buildable`
- **rimworld_research**: `current`, `list`, `set_current`, `stop`, `suggest`
- **rimworld_bills**: `list_workbenches`, `list`, `add`, `set`, `remove`, `reorder`
- **rimworld_zones**: `list`, `create_stockpile`, `create_growing`, `set_plant`, `expand`, `delete`
- **rimworld_storage**: `get_settings`, `set_priority`, `set_allowed`
- **rimworld_areas**: `list`, `create`, `modify`
- **rimworld_policies**: `list`, `assign`
- **rimworld_schedule**: `get`, `set`
- **rimworld_health**: `list_surgeries`, `pending_surgeries`, `add_surgery`, `cancel_surgery`
- **rimworld_animals**: `set_training`, `set_master`
- **rimworld_prisoners**: `list`, `set_interaction`, `release`, `execute`
- **rimworld_trade**: `list_traders`, `open`, `stock`, `set`, `evaluate`, `execute`, `close`
- **rimworld_caravan**: `list`, `sendable`, `form`
- **rimworld_world**: `factions`, `settlements`, `quests`
- **rimworld_ideology**: `list`
- **rimworld_map**: `info`, `things`, `region`
- **rimworld_query**: `search_defs`, `thing_info`, `recipe_info`
- **rimworld_analyze**: `best_pawn_for`, `idle_pawns`, `bottlenecks`, `threats`
- **rimworld_control**: `bridge_status`, `set_speed`, `set_run_in_background`, `save`, `notify`, `disable_bridge`

Backend only: `meta.ping`, `meta.list_commands`, `control.workshop_status`.

## Our exposed writes without a semantic executor domain

- `delete_buildings_bill_remove` — `/api/v1/buildings/bill/remove`
- `planning_create` — `/api/v2/planning/create`
- `planning_remove` — `/api/v2/planning/remove`
- `post_map_building_power` — `/api/v1/map/building/power`
- `post_pawn_edit_apparel` — `/api/v1/pawn/edit/apparel`
- `put_buildings_bill_reorder` — `/api/v1/buildings/bill/reorder`
- `put_buildings_bill_suspend` — `/api/v1/buildings/bill/suspend`
- `put_buildings_bill_update` — `/api/v1/buildings/bill/update`

Planning operations have an architect path, so their missing executor route is intentional. Other rows require individual review; do not blindly expose all editor APIs. The JSON inventory contains all 207 catalog entries with exposure and route flags.
