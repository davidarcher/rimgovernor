using System.Collections.Generic;

using Newtonsoft.Json;

using Newtonsoft.Json.Linq;

using RimBot.Tools;

namespace RimBot.Colony

{

    public static class ToolCatalog

    {

        private static ToolDefinition Define(string name, string description, string properties, params string[] required)

        {

            return new ToolDefinition { Name = name, Description = description, ParametersJson = new JObject

            { ["type"] = "object", ["properties"] = JObject.Parse(properties), ["required"] = new JArray(required), ["additionalProperties"] = false }.ToString(Formatting.None) };

        }

        public static List<ToolDefinition> Definitions() => new List<ToolDefinition>

        {

            Define("list_crops", "List researched sowable crops with exact names, fertility, growth time and skill requirements.", "{}"),
            Define("ensure_growing_zone", "Create a growing zone (max 8x8) near the base, selecting a crop from list_crops. Inspect fertility first. Reuses an existing covering zone and preserves other zones. Normal sowing/harvesting, weather and work rules apply.", "{x:{type:'integer'},z:{type:'integer'},width:{type:'integer',minimum:1,maximum:8},height:{type:'integer',minimum:1,maximum:8},crop:{type:'string'}}", "x","z","width","height","crop"),
            Define("list_workstations", "List built colony workstations, exact currently available production recipes and existing bills. Build a suitable stove/butcher/research bench normally if absent.", "{}"),
            Define("set_production_bill", "Set/update a production bill: mode targetCount (default target 10) or forever. Recipes with supportsTarget=false, such as butchering, require forever. Use IDs/recipe from list_workstations. Reuses an existing matching bill. Normal skill, fuel, power and ingredient requirements apply.", "{workstationId:{type:'integer'},recipe:{type:'string'},target:{type:'integer',minimum:1,maximum:1000},mode:{type:'string',enum:['targetCount','forever']}}", "workstationId","recipe"),
            Define("list_research", "List 25 unfinished research projects, exact names, prerequisites and whether each can start now.", "{}"),
            Define("start_research", "Select available research from list_research. Requires normal prerequisites and research facilities; does not grant research progress.", "{research:{type:'string'}}", "research"),
            Define("find_wildlife", "List 20 nearest visible wild animals with IDs, body size, predator flag and hunting designation. This is not a complete hunting-risk assessment; avoid dangerous prey.", "{}"),
            Define("hunt_animal", "Issue a normal hunting designation for a wild animal ID. Requires an enabled capable hunter with a ranged weapon. Hunting can cause retaliation; start with small nonpredators. Meat processing needs a butcher bill.", "{animalId:{type:'integer'}}", "animalId"),
            Define("equip_weapon", "Order an undrafted capable colonist to equip an allowed weapon item ID from find_items. Validates normal equipment restrictions and reach/reservation. Pawn must actually pick it up; this does not draft or teleport.", "{pawnId:{type:'integer'},itemId:{type:'integer'}}", "pawnId","itemId"),
            Define("find_shelter_options", "Compare nearby shelter options for 1–6 sleepers: reuse ruins/beds, close gaps against rock, shallow visible-rock excavation, or new shelter. Returns IDs, wall/material needs, mining/roof work and overhead-mountain risk, ranked by effort and distance. Inspect these BEFORE choosing a new room. Existing orders and structures are preserved.", "{sleepers:{type:'integer',minimum:1,maximum:6}}", "sleepers"),
            Define("prepare_shelter", "Execute a shelter option ID from find_shelter_options. Revalidates everything. If excavation is needed, designate normal mining first; wait, then call again with SAME ID. Once clear, fill gaps, add a door, roof and temporary sleeping spots, reusing existing beds. No instant mining/buildings and no hidden-room scan. Reuse the active shelterProject instead of starting another.", "{id:{type:'string'}}", "id"),
            Define("find_build_sites", "Get up to three clear sites near the persistent colony base, sorted nearest first. kind room returns 7x7 footprints; stockpile returns 4x4. Use these coordinates instead of inventing positions. Sites are revalidated when built.", "{kind:{type:'string',enum:['room','stockpile']}}", "kind"),
            Define("find_items", "Query ALL visible loose items on this map using its item index, nearest x,z first. Filter exact defName and/or category, forbidden any/yes/no. Returns IDs, coordinates, counts, total matches and pagination. Use for supplies instead of scanning areas. Distance is straight-line, not reachability.", "{x:{type:'integer'},z:{type:'integer'},defName:{type:'string'},category:{type:'string',enum:['all','food','material','weapon','apparel','medicine','other']},forbidden:{type:'string',enum:['any','yes','no']},offset:{type:'integer',minimum:0},limit:{type:'integer',minimum:1,maximum:40}}", "x","z"),

            Define("allow_item_ids", "Normal Allow order for up to 40 specific item stack IDs returned by find_items. Revalidates all IDs on the current map before changing anything. No hauling or teleporting.", "{ids:{type:'array',minItems:1,maxItems:40,items:{type:'integer'}}}", "ids"),

            Define("build_room", "Order a complete 7x7 room at exterior lower-left x,z, with south door, roof area and 0–4 beds with a clear aisle. Inspect the site first. Validates layout and allowed material quantities before placing. Uses stone blocks or wood walls and wood beds/door; never substitutes steel. Reuses matching orders. Fallback for a freestanding room AFTER comparing find_shelter_options. Prefer reusing nearby structure where economical.", "{x:{type:'integer'},z:{type:'integer'},beds:{type:'integer',minimum:0,maximum:4}}", "x","z","beds"),

            Define("designate_roof", "Set a normal Build Roof AREA, max 8x8. Roofs are not buildings or conduits and need no building lookup. Builders require supports and access; this only designates roofing.", "{x:{type:'integer'},z:{type:'integer'},width:{type:'integer',minimum:1,maximum:8},height:{type:'integer',minimum:1,maximum:8}}", "x","z","width","height"),

            Define("report_blocker", "Stop this review and visibly explain a missing capability or unresolved problem. Use when tools cannot establish a valid action; never guess substitutes or claim success.", "{reason:{type:'string',maxLength:500}}", "reason"),

            Define("inspect_area", "Inspect at most 8x8 cells. x,z is the lower left corner; map coordinates are absolute. Read before placing orders.", "{x:{type:'integer'},z:{type:'integer'},width:{type:'integer',minimum:1,maximum:8},height:{type:'integer',minimum:1,maximum:8}}", "x","z","width","height"),

            Define("allow_items", "Allow forbidden loose items in a selected visible area, max 8x8. Inspect first; choose useful supplies near the colony, not distant loot. This issues the normal player Allow order; it does not haul items.", "{x:{type:'integer'},z:{type:'integer'},width:{type:'integer',minimum:1,maximum:8},height:{type:'integer',minimum:1,maximum:8}}", "x","z","width","height"),

            Define("ensure_stockpile", "Create one shared default stockpile, only if the map has none. Repeated calls return the existing stockpile. Max 8x8, fully walkable empty area required. Does not change hauling priorities or unforbid items.", "{x:{type:'integer'},z:{type:'integer'},width:{type:'integer',minimum:1,maximum:8},height:{type:'integer',minimum:1,maximum:8}}", "x","z","width","height"),

            Define("list_buildables", "Find up to 15 researched buildable structures by name. Returns requiresMaterial and compatible materials with allowed/forbidden stock counts (up to 12). Use exact returned defName and material for construction.", "{search:{type:'string'}}", "search"),

            Define("place_blueprint", "Place a single shared construction blueprint after inspecting the site. Existing matching buildings/blueprints are returned without duplication. Does not spawn a completed building. If requiresMaterial is true, supply material from list_buildables; otherwise omit it.", "{defName:{type:'string',description:'Exact defName returned by list_buildables, e.g. Bed'},material:{type:'string',description:'Required for material-based buildings: exact material returned by list_buildables, e.g. WoodLog or Steel. Omit for buildings with requiresMaterial=false.'},x:{type:'integer'},z:{type:'integer'},rotation:{type:'integer',minimum:0,maximum:3}}", "defName","x","z","rotation"),

            Define("inspect_colonist", "Read one colonist's work priorities, incapabilities, and skills by ID from the colony summary.", "{pawnId:{type:'integer'}}", "pawnId"),

            Define("set_work_priority", "Set one colonist's work type priority, preserving normal incapabilities. 0 disables; 1 highest, 4 lowest. Manual priorities must already be enabled by the player.", "{pawnId:{type:'integer'},workType:{type:'string'},priority:{type:'integer',minimum:0,maximum:4}}", "pawnId","workType","priority"),

            Define("inspect_work_orders", "List up to 20 existing blueprints/frames and locations. Reuse pending orders instead of duplicating them.", "{}"),

            Define("set_plan", "Save a short shared plan for the next review. Include unfinished orders and what to wait for. Maximum 1200 characters.", "{plan:{type:'string',maxLength:1200}}", "plan")

        };

    }

}
