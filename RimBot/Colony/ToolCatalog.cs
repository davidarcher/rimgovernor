using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimBot.Tools;
namespace RimBot.Colony
{
    public static class ToolCatalog
    {
        private static ToolDefinition Define(string name, string description, string properties, params string[] required)
        {
            var schema=JObject.Parse(properties);
            if(new[]{"architect_build","bills_add","bills_configure","research_select","pawns_order","orders_allow_all","orders_allow","equipment_equip","zones_growing_designate"}.Contains(name))
                schema["projectId"]=new JObject{["type"]="string",["description"]="Optional exact strategic project id to link this order to tracked work."};
            return new ToolDefinition { Name = name, Description = description, ParametersJson = new JObject
            { ["type"] = "object", ["properties"] = schema, ["required"] = new JArray(required), ["additionalProperties"] = false }.ToString(Formatting.None) };
        }

        public static List<ToolDefinition> Definitions() => new List<ToolDefinition>
        {

            // Colony direction
            Define("manager_report_blocker", "Stop this review and visibly explain a missing capability or unresolved problem. Use when tools cannot establish a valid action; never guess substitutes or claim success.", "{reason:{type:'string',maxLength:500}}", "reason"),
            Define("manager_save_plan", "Save a short shared plan for the next review. Include unfinished orders and what to wait for. Maximum 1200 characters.", "{plan:{type:'string',maxLength:1200}}", "plan"),

            // Architect and construction
            Define("architect_build", "Architect > Build: use native placement validation and designation. Buildings with zero construction work are placed immediately, as in the game UI. Other buildings become construction orders. Supply exact discovered definition/material names.", "{defName:{type:'string',description:'Exact defName returned by architect_buildables, e.g. Bed'},material:{type:'string',description:'Required for material-based buildings: exact material returned by architect_buildables, as returned by architect_materials. Omit for buildings with requiresMaterial=false.'},x:{type:'integer'},z:{type:'integer'},rotation:{type:'integer',minimum:0,maximum:3}}", "defName","x","z","rotation"),
            Define("architect_buildables", "Read building definitions available in Architect, filtered by name. Exact definition/label matches take precedence; otherwise up to 8 matches. Three material samples; architect_materials lists all. Includes footprints and existing/pending counts.", "{search:{type:'string'}}", "search"),
            Define("architect_catalog", "Read native Architect categories and their allowed designators, using game labels and class names. Reports which controls have an adapter; listing a control does not imply it can be executed.", "{category:{type:'string'}}"),
            Define("architect_materials", "Query ALL compatible materials from the selected building's game definition, paginated, with available/forbidden stock counts. No preset material list. Use exact returned names.", "{defName:{type:'string'},offset:{type:'integer',minimum:0},limit:{type:'integer',minimum:1,maximum:20}}","defName"),
            Define("areas_build_roof", "Zones > Build roof area: native designation, including clearing conflicting Remove roof cells. This is an order; roof support and construction remain game responsibilities.", "{x:{type:'integer'},z:{type:'integer'},width:{type:'integer',minimum:1},height:{type:'integer',minimum:1}}", "x","z","width","height"),
            Define("construction_list", "Read existing blueprints and frames, with definition names, IDs, positions and work done. Returns up to 20 orders.", "{}"),

            // Orders
            Define("orders_allow", "Normal Allow order for up to 40 specific item stack IDs returned by items_list. Revalidates all IDs on the current map before changing anything. No hauling or teleporting.", "{ids:{type:'array',minItems:1,maxItems:40,items:{type:'integer'}}}", "ids"),
            Define("orders_allow_all", "Orders > Allow, right-click > Unforbid all items: invoke the native map-wide action for all currently visible forbidden items. Includes distant food, insect jelly and dangerous-area supplies: can send normal workers into danger. Not routine supply preparation or a completion goal. Does not assess safety.", "{}"),
            Define("orders_allow_area", "Orders > Allow: apply the native Allow command to visible loose items in a rectangle.", "{x:{type:'integer'},z:{type:'integer'},width:{type:'integer',minimum:1},height:{type:'integer',minimum:1}}", "x","z","width","height"),
            Define("orders_hunt", "Orders > Hunt: apply the native hunting designation and warnings to a visible pawn ID. Hunters and equipment affect execution, not permission to place the order.", "{animalId:{type:'integer'}}", "animalId"),

            // Zones and storage
            Define("storage_configure", "Storage tab: change a stockpile or shelf's settings. Supply one target ID. Optional copyFrom applies first, reset second, then ordered filter rules and ranges. Unspecified settings remain unchanged. Uses native StorageSettings and ThingFilter; fixed storage restrictions still apply.", "{zoneId:{type:'integer'},thingId:{type:'integer'},copyFrom:{type:'object',properties:{zoneId:{type:'integer'},thingId:{type:'integer'}},additionalProperties:false},reset:{type:'string',enum:['unchanged','nothing','everything']},priority:{type:'string'},rules:{type:'array',items:{type:'object',properties:{kind:{type:'string',enum:['thing','category','special']},defName:{type:'string'},allow:{type:'boolean'}},required:['kind','defName','allow'],additionalProperties:false}},qualityMin:{type:'string'},qualityMax:{type:'string'},hitPointsMin:{type:'number',minimum:0,maximum:1},hitPointsMax:{type:'number',minimum:0,maximum:1}}"),
            Define("storage_filter_options", "Discover native storable item, category and special-filter definitions by name. Paginated; use exact names for storage_configure.", "{kind:{type:'string',enum:['thing','category','special']},search:{type:'string'},offset:{type:'integer',minimum:0},limit:{type:'integer',minimum:1,maximum:40}}"),
            Define("storage_inspect", "Storage tab: omit target IDs to list actual stockpile zones and storage buildings. Supply returned zoneId OR thingId to read filters/priority. Loose stacks are items, not storage targets.", "{zoneId:{type:'integer'},thingId:{type:'integer'},offset:{type:'integer',minimum:0},limit:{type:'integer',minimum:1,maximum:40}}"),
            Define("zones_growing_designate", "Zones > Growing zone: drag a rectangle; native-invalid cells are skipped. Select crop using the exact defName from plants_sowable. Optional zoneId selects the zone to extend. No grower staffing requirement for placing a zone.", "{zoneId:{type:'integer'},x:{type:'integer'},z:{type:'integer'},width:{type:'integer',minimum:1},height:{type:'integer',minimum:1},crop:{type:'string'}}", "x","z","width","height","crop"),
            Define("zones_list", "Read current zones with native IDs, names, type, size and bounding coordinates. Returns all current map zones; use IDs when extending, trimming or configuring.", "{}"),
            Define("zones_remove_cells", "Zones > Delete zone: remove the rectangle's cells from the specified zone only. Native handling splits disconnected zones and removes an empty zone. Stored items are not removed.", "{zoneId:{type:'integer'},x:{type:'integer'},z:{type:'integer'},width:{type:'integer',minimum:1},height:{type:'integer',minimum:1}}","zoneId","x","z","width","height"),
            Define("zones_stockpile_designate", "Zones > Stockpile zone: designate a rectangle using the native stockpile tool. Optional zoneId selects the stockpile to extend; does not enforce a single stockpile per map.", "{zoneId:{type:'integer'},x:{type:'integer'},z:{type:'integer'},width:{type:'integer',minimum:1},height:{type:'integer',minimum:1}}", "x","z","width","height"),

            // Selection and native actions
            Define("selection_inspect", "Select a visible targetId: inspect its state, material needs, nearby supplies/colonists and local geometry. Add pawnId to get native action handles for that pawn and target.", "{targetId:{type:'integer'},pawnId:{type:'integer'}}","targetId"),

            // Pawns and work
            Define("equipment_equip", "Pawn right-click > Equip: use the native menu option, checks and ordered job. Works drafted or undrafted; permits the native Allow behavior. Native confirmation dialogs require player interaction.", "{pawnId:{type:'integer'},itemId:{type:'integer'}}", "pawnId","itemId"),
            Define("pawns_inspect", "Read one section of a visible pawn's actual game state, including canFight and canTakeOrder. needs: need levels; mood: active thoughts; social: traits/relations/opinions; health: conditions/capacities; equipment: weapons/apparel; work: skills/priorities; animal: age/training. Each list is paginated (default 10); pages gives totals/next offsets. No simulation or changes.", "{pawnId:{type:'integer'},section:{type:'string',enum:['needs','mood','social','health','equipment','work','animal']},offset:{type:'integer',minimum:0},limit:{type:'integer',minimum:1,maximum:20}}", "pawnId","section"),
            Define("pawns_list", "Query all visible spawned pawns on the current map, including animals. Exact kind filter; paginated, stable IDs. Optional x,z sorts by straight-line distance, not reachability. Use pawns_inspect for details.", "{group:{type:'string',enum:['all','colonists','prisoners','animals','colony_animals','wild_animals','hostiles']},kind:{type:'string'},x:{type:'integer'},z:{type:'integer'},offset:{type:'integer',minimum:0},limit:{type:'integer',minimum:1,maximum:40}}"),
            Define("pawns_order", "Execute one actionId returned by selection_inspect or pawns_orders. The handle remembers pawn and target. Revalidates the native menu before issuing the order; does not claim completion.", "{actionId:{type:'string'}}","actionId"),
            Define("pawns_orders", "Read current job with pawnId alone. Add targetId OR both x,z for the full native right-click menu: prioritized construction/work, hauling, equipment, clothing, ingestion, rescue, combat and available mod/DLC options, including disabled reasons. Pass a returned actionId to pawns_order.", "{pawnId:{type:'integer'},targetId:{type:'integer'},x:{type:'integer'},z:{type:'integer'}}","pawnId"),
            Define("pawns_set_drafted", "Pawn Draft/Undraft toggle: set enabled true to draft, false to undraft using the native command and its disabled checks. No automatic drafting decisions are made by this tool.", "{pawnId:{type:'integer'},enabled:{type:'boolean'}}","pawnId","enabled"),
            Define("pawns_set_fire_at_will", "Pawn Fire at will toggle: use the native command. Available only when the game exposes it (normally drafted with a ranged weapon).", "{pawnId:{type:'integer'},enabled:{type:'boolean'}}","pawnId","enabled"),
            Define("work_set_priority", "Set one colonist's work type priority, preserving normal incapabilities. 0 disables; 1 highest, 4 lowest. Manual priorities must already be enabled by the player.", "{pawnId:{type:'integer'},workType:{type:'string'},priority:{type:'integer',minimum:0,maximum:4}}", "pawnId","workType","priority"),

            // Map and inventory
            Define("buildings_list", "List built and pending player structures by exact defName (optional), with IDs and coordinates. Paginated. Check existing facilities before ordering more.", "{defName:{type:'string'},offset:{type:'integer',minimum:0},limit:{type:'integer',minimum:1,maximum:20}}"),
            Define("items_list", "Query ALL visible loose items on this map using its item index, nearest x,z first. Filter exact defName and/or category, forbidden any/yes/no. Returns IDs, coordinates, counts, total matches and pagination. Use for supplies instead of scanning areas. Distance is straight-line, not reachability.", "{x:{type:'integer'},z:{type:'integer'},defName:{type:'string'},category:{type:'string',enum:['all','food','material','weapon','apparel','medicine','other']},forbidden:{type:'string',enum:['any','yes','no']},offset:{type:'integer',minimum:0},limit:{type:'integer',minimum:1,maximum:40}}", "x","z"),
            Define("map_inspect", "Inspect at most 8x8 cells. x,z is the lower left corner; map coordinates are absolute. Read before placing orders.", "{x:{type:'integer'},z:{type:'integer'},width:{type:'integer',minimum:1,maximum:8},height:{type:'integer',minimum:1,maximum:8}}", "x","z","width","height"),
            Define("rooms_list", "Read actual enclosed rooms from RimWorld's room system: roof gaps, role, temperature, size and an example cell. Outdoor regions are excluded. Paginated; inspect the area for geometry.", "{offset:{type:'integer',minimum:0},limit:{type:'integer',minimum:1,maximum:20}}"),

            // Growing and production
            Define("bills_add", "Bills > Add bill: create the selected available recipe with native defaults. Returns a billId. Repeating this action creates another bill; inspect existing bills first.", "{workstationId:{type:'integer'},recipe:{type:'string'}}", "workstationId","recipe"),
            Define("bills_configure", "Bills: configure a specific production bill by ID. Choose the native repeat mode returned by bills_list. Count means repetitions or target inventory for that mode. Uses the game recipe counter to validate target support.", "{workstationId:{type:'integer'},billId:{type:'string'},mode:{type:'string'},count:{type:'integer',minimum:0}}", "workstationId","billId","mode"),
            Define("bills_list", "List built colony workstations, exact currently available production recipes and existing bills. Build a suitable stove/butcher/research bench normally if absent.", "{}"),
            Define("plants_sowable", "Read researched sowable plant definitions, harvest products, yields, edibility, sow tags and growth requirements. Lower fertilitySensitivity means less penalty on poor ground. Plants without a harvest product do not produce harvested food.", "{}"),

            // Research
            Define("research_list", "List 25 unfinished research projects, exact names, prerequisites and whether each can start now.", "{}"),
            Define("research_select", "Select available research from research_list. Requires normal prerequisites and research facilities; does not grant research progress.", "{research:{type:'string'}}", "research"),

            // Notifications
            Define("notifications_read", "Read the player's native alerts, letters and recent messages. Includes explanation text; does not dismiss or answer notifications. May refer to other maps.", "{}"),
        };
    }
}
