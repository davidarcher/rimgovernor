using System.Collections.Generic;
namespace RimBot.Colony {
    public static class ToolNames {
        private static readonly Dictionary<string,string> OldNames=new Dictionary<string,string> {
            {"discover_tools","tools_search"},
            {"enable_tools","tools_enable"},
            {"inspect_notifications","notifications_read"},
            {"list_materials","architect_materials"},
            {"find_rooms","rooms_list"},
            {"find_buildings","buildings_list"},
            {"find_pawns","pawns_list"},
            {"inspect_pawn","pawns_inspect"},
            {"list_crops","plants_sowable"},
            {"ensure_growing_zone","zones_growing_designate"},
            {"list_workstations","bills_list"},
            {"set_production_bill","bills_configure"},
            {"list_research","research_list"},
            {"start_research","research_select"},
            {"hunt_animal","orders_hunt"},
            {"equip_weapon","equipment_equip"},
            {"find_items","items_list"},
            {"allow_item_ids","orders_allow"},
            {"designate_roof","areas_build_roof"},
            {"report_blocker","manager_report_blocker"},
            {"inspect_area","map_inspect"},
            {"allow_items","orders_allow_area"},
            {"ensure_stockpile","zones_stockpile_designate"},
            {"list_buildables","architect_buildables"},
            {"place_blueprint","architect_build"},
            {"set_work_priority","work_set_priority"},
            {"inspect_work_orders","construction_list"},
            {"set_plan","manager_save_plan"},
        };
        public static string Canonical(string name)=>OldNames.TryGetValue(name,out var current)?current:name;
    }
}