using System;

using System.Collections.Generic;

using System.Linq;

using Newtonsoft.Json;

using Newtonsoft.Json.Linq;

using RimBot.Tools;

using RimWorld;

using Verse;



namespace RimBot.Colony

{

    public static class ColonyTools

    {

        public const int MaxActions = 4;

        public static bool IsAction(string name) => ColonyDevelopment.IsAction(name) || name=="pawns_set_drafted" || name=="pawns_set_fire_at_will" || name=="pawns_order" || name=="storage_configure" || name=="zones_remove_cells" || name == "orders_allow_all" || name == "orders_allow" || name == "areas_build_roof" || name == "orders_allow_area" || name == "zones_stockpile_designate" || name == "architect_build" || name == "work_set_priority";

        private static int Number(JObject args, string key)

        {

            if (args[key]?.Type != JTokenType.Integer) throw new ArgumentException("Missing integer " + key);

            return args[key].Value<int>();

        }

        private static string Text(JObject args, string key)

        {

            if (args[key]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(args[key].Value<string>()))

                throw new ArgumentException("Missing text " + key);

            return args[key].Value<string>();

        }

        private static List<IntVec3> Area(Map map, JObject args,bool query=true)

        {

            int x = Number(args,"x"), z = Number(args,"z"), w = Number(args,"width"), h = Number(args,"height");

            if (w < 1 || h < 1 || (query && (w > 8 || h > 8)) || x < 0 || z < 0 || x > map.Size.x-w || z > map.Size.z-h)

                throw new ArgumentException("Area must fit the map. Observation pages are limited to 8x8 cells.");

            return CellRect.FromLimits(x,z,x+w-1,z+h-1).Cells.ToList();

        }

        public static string Execute(Map map, ToolCall call, Action<string> savePlan)

        {

            var a = call.Arguments ?? new JObject();

            switch (call.Name)

            {

                case "pawns_set_drafted": return PawnDirectOrders.Toggle(map,a,false);
                case "pawns_set_fire_at_will": return PawnDirectOrders.Toggle(map,a,true);
                case "selection_inspect": return SelectionInspection.Read(map,a).ToString(Formatting.None);
                case "pawns_orders": return PawnDirectOrders.Inspect(map,a).ToString(Formatting.None);
                case "pawns_order": return PawnDirectOrders.Execute(map,a);
                case "storage_inspect": return StorageTools.Inspect(map,a).ToString(Formatting.None);
                case "storage_filter_options": return StorageTools.Options(a).ToString(Formatting.None);
                case "storage_configure": return StorageTools.Configure(map,a);
                case "zones_list": return new JArray(map.zoneManager.AllZones.Select(z=>new JObject {
                    ["id"]=z.ID,["name"]=z.label,["type"]=z.GetType().Name,["cells"]=z.Cells.Count,
                    ["minX"]=z.Cells.Count>0?z.Cells.Min(c=>c.x):0,["minZ"]=z.Cells.Count>0?z.Cells.Min(c=>c.z):0,
                    ["maxX"]=z.Cells.Count>0?z.Cells.Max(c=>c.x):0,["maxZ"]=z.Cells.Count>0?z.Cells.Max(c=>c.z):0
                })).ToString(Formatting.None);
                case "zones_remove_cells":
                    PlayerOrders.RequireMap(map);
                    int zoneId=Number(a,"zoneId");
                    var remove=Area(map,a,false).Where(c=>map.zoneManager.ZoneAt(c)?.ID==zoneId).ToList();
                    new Designator_ZoneDelete().DesignateMultiCell(remove);
                    return "Removed "+remove.Count+" zone cells.";
                case "architect_catalog": return ArchitectCatalog.Read(a.Value<string>("category")).ToString(Formatting.None);
                case "notifications_read": return ColonyNotifications.Read(true).ToString(Formatting.None);
                case "architect_materials": return MaterialQuery(map,a).ToString(Formatting.None);
                case "rooms_list": return BuildingQueries.Rooms(map,a).ToString(Formatting.None);
                case "buildings_list": return BuildingQueries.Find(map,a).ToString(Formatting.None);
                case "pawns_list": return PawnQueries.Find(map,a).ToString(Formatting.None);
                case "pawns_inspect": return PawnQueries.Inspect(map,Number(a,"pawnId"),Text(a,"section"),a["offset"]==null?0:Number(a,"offset"),a["limit"]==null?10:Number(a,"limit")).ToString(Formatting.None);
                case "architect_preview": return ConstructionLayout.Preview(map,a).ToString(Formatting.None);
                case "map_inspect":
                    if(a.Value<string>("format")=="grid") return SpatialView.Read(map,Number(a,"x"),Number(a,"z"),Number(a,"width"),Number(a,"height")).ToString(Formatting.None);

                    return new JArray(Area(map,a).Select(c => new JObject { ["x"]=c.x,["z"]=c.z,

                        ["fertility"]=c.GetFertility(map),["terrain"]=c.GetTerrain(map).defName,["walkable"]=c.Walkable(map),["roofed"]=c.Roofed(map),

                        ["zone"]=map.zoneManager.ZoneAt(c)?.label ?? "",

                        ["things"]=new JArray(c.GetThingList(map).Take(6).Select(t=>new JObject { ["id"]=t.thingIDNumber,["defName"]=t.def.defName,["count"]=t.stackCount,["forbidden"]=t.IsForbidden(Faction.OfPlayer) })) })).ToString(Formatting.None);

                case "items_list":

                    int originX=Number(a,"x"),originZ=Number(a,"z");

                    if(!new IntVec3(originX,0,originZ).InBounds(map)) throw new ArgumentException("Query origin is outside this map.");

                    return ItemQuery.Find(map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)

                        .Where(t=>t.def.category==ThingCategory.Item && !t.Position.Fogged(map)).Select(t=>new ItemRecord {

                            Id=t.thingIDNumber,DefName=t.def.defName,Count=t.stackCount,X=t.Position.x,Z=t.Position.z,Forbidden=t.IsForbidden(Faction.OfPlayer),

                            Category=t.def.IsMedicine?"medicine":t.def.IsNutritionGivingIngestible?"food":t.def.stuffProps!=null?"material":t.def.IsWeapon?"weapon":t.def.IsApparel?"apparel":"other"

                        }),a["defName"]?.Value<string>(),a["category"]?.Value<string>()??"all",a["forbidden"]?.Value<string>()??"any",originX,originZ,

                        a["offset"]==null?0:Number(a,"offset"),a["limit"]==null?20:Number(a,"limit")).ToString(Formatting.None);

                case "orders_allow_all": return PlayerOrders.AllowAll(map);
                case "orders_allow":

                    var ids=a["ids"] as JArray;

                    if(ids==null || ids.Count<1 || ids.Count>40 || ids.Any(id=>id.Type!=JTokenType.Integer)) throw new ArgumentException("Supply 1–40 item IDs from items_list.");

                    var requested=new HashSet<int>(ids.Values<int>());

                    var found=map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).Where(t=>requested.Contains(t.thingIDNumber) && t.def.category==ThingCategory.Item && !t.Position.Fogged(map)).ToList();

                    if(found.Count!=requested.Count) throw new ArgumentException("Requested item IDs are not present among visible loose items on this map. Copy exact items[].id values from items_list; IDs are not row numbers and cannot be guessed. To allow every visible item, use orders_allow_all with no arguments. Nothing changed.");

                    int changed=found.Count(t=>t.IsForbidden(Faction.OfPlayer));

                    PlayerOrders.Allow(map,found);

                    return "Allowed " + changed + " selected item stacks; " + (found.Count-changed) + " already allowed. Normal hauling and reachability rules apply.";

                case "manager_report_blocker":

                    return "Blocked: " + Text(a,"reason");

case "areas_build_roof":

                    var roofCells=Area(map,a,false);

                    if(roofCells.Any(c=>c.Fogged(map))) throw new ArgumentException("Cannot designate a roof in fogged cells.");

                    PlayerOrders.Roof(map,roofCells);

                    return "Designated " + roofCells.Count + " cells for roofing. Builders need normal roof supports and access. A designation is not a completed roof.";

                case "orders_allow_area":

                    var selected = Area(map,a,false).SelectMany(c=>c.GetThingList(map)).Distinct()

                        .Where(t=>t.def.category==ThingCategory.Item && t.IsForbidden(Faction.OfPlayer) && !t.Position.Fogged(map)).ToList();

                    PlayerOrders.Allow(map,selected);

                    return "Allowed " + selected.Count + " item stacks in the selected area. Normal hauling, access and work rules still apply.";

                case "zones_stockpile_designate":

                    return PlayerOrders.Zone(map,Area(map,a,false),false,zoneId:a.Value<int?>("zoneId"));

                case "architect_buildables":

                    string query = Text(a,"search");

                    if(query.Trim().Equals("roof",StringComparison.OrdinalIgnoreCase) || query.Trim().Equals("roofing",StringComparison.OrdinalIgnoreCase))

                        throw new ArgumentException("Roofs are area designations. Use areas_build_roof, not a building or conduit.");

                    var available=DefDatabase<ThingDef>.AllDefs.Where(d=>d.designationCategory!=null && d.category==ThingCategory.Building && BuildCopyCommandUtility.FindAllowedDesignator(d)!=null).ToList();
                    var exact=available.Where(d=>d.defName.Equals(query,StringComparison.OrdinalIgnoreCase) || d.label.Equals(query,StringComparison.OrdinalIgnoreCase)).ToList();
                    var matches=exact.Count>0?exact:available.Where(d=>d.defName.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0 || d.label.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0);
                    return new JArray(matches.OrderBy(d=>d.defName).Take(8).Select(d=>new JObject{
                        ["defName"]=d.defName,["label"]=d.label,["existing"]=BuildingQueries.Counts(map,d.defName),
                        ["sizeX"]=d.size.x,["sizeZ"]=d.size.z,["requiresMaterial"]=d.MadeFromStuff,
                        ["workToBuildBase"]=d.GetStatValueAbstract(StatDefOf.WorkToBuild),
                        ["instantPlacement"]=d.GetStatValueAbstract(StatDefOf.WorkToBuild)==0,
                        ["fixedResourceCosts"]=new JArray((d.costList??new List<ThingDefCountClass>()).Select(c=>new JObject{["defName"]=c.thingDef.defName,["count"]=c.count})),
                        ["stuffCount"]=d.costStuffCount,
                        ["sleepingSlots"]=typeof(Building_Bed).IsAssignableFrom(d.thingClass)?BedUtility.GetSleepingSlotsCount(d.size):0,
                        ["sleepingFor"]=typeof(Building_Bed).IsAssignableFrom(d.thingClass)?(d.building.bed_humanlike?"humanlike":"animals"):null,
                        ["materials"]=Materials(map,d,3),["moreMaterials"]=d.MadeFromStuff?"architect_materials":null,
                        ["placeOrder"]=new JObject{["tool"]="architect_build",["defName"]=d.defName,["needs"]=new JArray(d.MadeFromStuff?new[]{"x","z","rotation","material"}:new[]{"x","z","rotation"})},
                        ["note"]="Definition only. Zero work means instant placement with no builder job. Empty fixedResourceCosts and zero stuffCount mean no resources required. Other placements require normal construction. Work can vary by material."
                    })).ToString(Formatting.None);

                case "architect_build":

                    return Build(map,a);

                case "construction_list":

                    return ColonyObserver.Orders(map).ToString(Formatting.None);

                case "work_set_priority":

                    var worker = Pawn(map,a);

                    var work = DefDatabase<WorkTypeDef>.GetNamedSilentFail(Text(a,"workType"));

                    int priority = Number(a,"priority");

                    if (priority<0 || priority>4 || work==null || worker.workSettings==null || worker.WorkTypeIsDisabled(work))

                        throw new ArgumentException("Invalid priority/work type, or colonist is incapable. Nothing changed.");

                    if (!Find.PlaySettings.useWorkPriorities) throw new ArgumentException("Player must enable manual work priorities in the Work tab first.");

                    worker.workSettings.SetPriority(work,priority);

                    return worker.LabelShort + ": " + work.defName + " priority=" + priority;

                case "manager_save_plan":

                    string plan = Text(a,"plan");

                    if(plan.Length>1200) throw new ArgumentException("Plan exceeds 1200 characters.");

                    savePlan(plan); return "Shared plan saved.";

                default: return ColonyDevelopment.Execute(map,call.Name,a);

            }

        }

        private static Pawn Pawn(Map map,JObject a)

        {

            int id=Number(a,"pawnId");

            return map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p=>p.thingIDNumber==id) ?? throw new ArgumentException("Colonist is no longer on this map.");

        }

        private static JObject MaterialQuery(Map map,JObject a)
        {
            var building=DefDatabase<ThingDef>.GetNamedSilentFail(Text(a,"defName"));
            if(building==null || building.category!=ThingCategory.Building) throw new ArgumentException("Choose a building from architect_buildables.");
            int offset=a["offset"]==null?0:Number(a,"offset"),limit=a["limit"]==null?12:Number(a,"limit");
            if(offset<0 || limit<1 || limit>20) throw new ArgumentException("Use offset >= 0 and limit 1–20.");
            var materials=Materials(map,building,int.MaxValue);
            return new JObject{["requiresMaterial"]=building.MadeFromStuff,["total"]=materials.Count,
                ["nextOffset"]=offset+limit<materials.Count?(JToken)(offset+limit):JValue.CreateNull(),["materials"]=new JArray(materials.Skip(offset).Take(limit))};
        }
        private static JArray Materials(Map map, ThingDef building,int limit=12)

        {

            if(!building.MadeFromStuff) return new JArray();

            var stocks=map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)

                .Where(t=>t.def.category==ThingCategory.Item && !t.Position.Fogged(map)).ToList();

            return new JArray(DefDatabase<ThingDef>.AllDefs.Where(d=>d.stuffProps!=null && d.stuffProps.CanMake(building))

                .Select(d=>new { Def=d, Allowed=stocks.Where(t=>t.def==d && !t.IsForbidden(Faction.OfPlayer)).Sum(t=>t.stackCount),

                    Forbidden=stocks.Where(t=>t.def==d && t.IsForbidden(Faction.OfPlayer)).Sum(t=>t.stackCount) })

                .OrderByDescending(s=>s.Allowed).ThenByDescending(s=>s.Forbidden).ThenBy(s=>s.Def.defName).Take(limit)

                .Select(s=>new JObject { ["material"]=s.Def.defName,["label"]=s.Def.label,["allowed"]=s.Allowed,["forbidden"]=s.Forbidden }));

        }

        private static string Build(Map map,JObject a)
        {
            if(a["toX"]==null && a["toZ"]==null) return BuildOne(map,a);
            var placements=ConstructionLayout.Expand(a); ConstructionLayout.Validate(map,placements);
            return new JArray(placements.Select(p=>BuildOne(map,p))).ToString(Formatting.None);
        }
        private static string BuildOne(Map map,JObject a)

        {

            var def=DefDatabase<ThingDef>.GetNamedSilentFail(Text(a,"defName"));

            int rotation=Number(a,"rotation");

            var cell=new IntVec3(Number(a,"x"),0,Number(a,"z"));

            if (def==null || def.designationCategory==null || def.category!=ThingCategory.Building || def.blueprintDef==null ||

                rotation<0 || rotation>3 || !cell.InBounds(map) || (def.researchPrerequisites!=null && def.researchPrerequisites.Any(r=>!r.IsFinished)))

                throw new ArgumentException("Unknown, unavailable, or invalid construction order.");

            if(cell.GetThingList(map).Any(t=>t.def==def || t.def.entityDefToBuild==def)) return "Matching building or construction order already exists. Nothing added.";

            ThingDef stuff=null;

            if(def.MadeFromStuff)

            {

                string material=(a["material"] ?? a["stuff"])?.Value<string>(); // Accept saved proposals from earlier builds.

                stuff=string.IsNullOrWhiteSpace(material) ? null : DefDatabase<ThingDef>.GetNamedSilentFail(material);

                if(stuff?.stuffProps==null || !stuff.stuffProps.CanMake(def))

                    throw new ArgumentException("Material is required for " + def.defName + ". Choose a material from these compatible options: " + Materials(map,def).ToString(Formatting.None) + ". Nothing changed.");

            }

            var rot=new Rot4(rotation);

            var report=GenConstruct.CanPlaceBlueprintAt(def,cell,rot,map);

            if(!report.Accepted) throw new ArgumentException(report.Reason ?? "Cannot build at this cell.");

            PlayerConstruction.Place(def,cell,map,rot,stuff);

            var placed=cell.GetThingList(map).FirstOrDefault(t=>t.Position==cell && t.def==def && t is Building);
            return placed!=null ? "Placed immediately: "+def.defName+" at "+cell+"; id="+placed.thingIDNumber+". Complete; no construction job is pending."
                : "Construction ordered: " + def.defName + " at " + cell + ". Wait for normal construction work.";

        }



        public static JObject Snapshot(Map map)

        {

            var pawns=map.mapPawns.FreeColonistsSpawned;

            // Indexed item list, not a cell-by-cell map scan. Sampled at most every 10 real seconds.

            var items=map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver);

            var stocks=items.Where(t=>t.def.category==ThingCategory.Item && !t.Position.Fogged(map)).GroupBy(t=>t.def.defName)

                .Select(g=>new { name=g.Key,count=g.Sum(t=>t.stackCount),forbidden=g.Where(t=>t.IsForbidden(Faction.OfPlayer)).Sum(t=>t.stackCount) }).OrderByDescending(x=>x.count).Take(20);

            return new JObject {

                ["base"]=ColonyLocation.Describe(map),["mapId"]=map.uniqueID,["mapWidth"]=map.Size.x,["mapHeight"]=map.Size.z,

                ["colonistCount"]=pawns.Count,["colonistsTruncated"]=pawns.Count>30,

                ["colonists"]=new JArray(pawns.Take(30).Select(p=>new JObject { ["id"]=p.thingIDNumber,["name"]=p.LabelShort,

                    ["x"]=p.Position.x,["z"]=p.Position.z,["downed"]=p.Downed,["drafted"]=p.Drafted,["weapon"]=p.equipment?.Primary?.def.defName??"none",["canFight"]=!p.WorkTagIsDisabled(WorkTags.Violent),["idle"]=p.mindState.IsIdle,["job"]=p.CurJob?.def.defName })),

                ["visibleHostileFactionPawns"]=PawnQueries.Find(map,new JObject{["group"]="hostiles",["x"]=map.GetComponent<ColonyLocation>().Center.x,["z"]=map.GetComponent<ColonyLocation>().Center.z,["limit"]=8}),
                ["hostilityNote"]="Faction relationship only. Presence on the map does not establish an attack or prevent unrelated orders. Jobs and targets describe current activity, not a full threat assessment.",

                ["looseItemsTop20"]=new JArray(stocks.Select(s=>new JObject { ["defName"]=s.name,["count"]=s.count,["forbidden"]=s.forbidden,["allowed"]=s.count-s.forbidden })),

                ["forbiddenItemsSample"]=new JArray(items.Where(t=>t.def.category==ThingCategory.Item && !t.Position.Fogged(map) && t.IsForbidden(Faction.OfPlayer)).Take(12).Select(t=>new JObject { ["id"]=t.thingIDNumber,["defName"]=t.def.defName,["count"]=t.stackCount,["x"]=t.Position.x,["z"]=t.Position.z })),

                ["stockpileCount"]=map.zoneManager.AllZones.OfType<Zone_Stockpile>().Count(),

                ["stockpiles"]=new JArray(map.zoneManager.AllZones.OfType<Zone_Stockpile>().Take(10).Select(z=>new JObject

                    { ["id"]=z.ID,["name"]=z.label,["cells"]=z.Cells.Count,["x"]=z.Cells.FirstOrDefault().x,["z"]=z.Cells.FirstOrDefault().z })),

                ["blueprints"]=map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count,

                ["frames"]=map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count,

                ["buildings"]=BuildingQueries.Counts(map),
                ["pendingWork"]=ColonyObserver.Orders(map),
                ["growingZones"]=new JArray(map.zoneManager.AllZones.OfType<Zone_Growing>().Select(g=>new JObject { ["id"]=g.ID,["crop"]=g.GetPlantDefToGrow().defName,["cells"]=g.Cells.Count })),
                ["notifications"]=ColonyNotifications.Read(),
                ["activeResearch"]=Find.ResearchManager.GetProject()?.defName ?? "none",

                ["note"]="Forbidden items cannot be used or hauled until allowed. Allowed counts do not guarantee reachability. Existing stockpiles are real zones, not proposals. activeResearch is only the current project, not unlocked technology. Pawns execute normal game jobs."

            };

        }

        public static string Fingerprint(JObject snapshot)

        {

            var copy=(JObject)snapshot.DeepClone();

            copy.Remove("localMap"); copy.Remove("workFocus"); copy.Remove("repeatedObservations");
            copy.Remove("objectives"); // Objective state transitions are fingerprinted separately.

            foreach(var pawn in (JArray)copy["colonists"]) { ((JObject)pawn).Remove("x"); ((JObject)pawn).Remove("z"); }

            // Resource buckets avoid a model request for each individual consumed item.

            foreach(var stock in (JArray)copy["looseItemsTop20"]) stock["count"]=stock["count"].Value<int>()/25;

            return copy.ToString(Formatting.None);

        }

    }

}
