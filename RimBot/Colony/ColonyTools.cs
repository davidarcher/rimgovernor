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

        public static bool IsAction(string name) => ColonyDevelopment.IsAction(name) || name == "prepare_shelter" || name == "allow_item_ids" || name == "build_room" || name == "designate_roof" || name == "allow_items" || name == "ensure_stockpile" || name == "place_blueprint" || name == "set_work_priority";

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

        private static List<IntVec3> Area(Map map, JObject args)

        {

            int x = Number(args,"x"), z = Number(args,"z"), w = Number(args,"width"), h = Number(args,"height");

            if (w < 1 || w > 8 || h < 1 || h > 8 || x < 0 || z < 0 || x > map.Size.x-w || z > map.Size.z-h)

                throw new ArgumentException("Area must be 1–8 cells on each side and within the map.");

            return CellRect.FromLimits(x,z,x+w-1,z+h-1).Cells.ToList();

        }

        public static string Execute(Map map, ToolCall call, Action<string> savePlan)

        {

            var a = call.Arguments ?? new JObject();

            switch (call.Name)

            {

                case "inspect_area":

                    return new JArray(Area(map,a).Select(c => new JObject { ["x"]=c.x,["z"]=c.z,

                        ["fertility"]=c.GetTerrain(map).fertility,["terrain"]=c.GetTerrain(map).defName,["walkable"]=c.Walkable(map),["roofed"]=c.Roofed(map),

                        ["zone"]=map.zoneManager.ZoneAt(c)?.label ?? "",

                        ["things"]=new JArray(c.GetThingList(map).Take(6).Select(t=>new JObject { ["id"]=t.thingIDNumber,["defName"]=t.def.defName,["count"]=t.stackCount,["forbidden"]=t.IsForbidden(Faction.OfPlayer) })) })).ToString(Formatting.None);

                case "find_shelter_options":
                    return map.GetComponent<ShelterPlanner>().Find(Number(a,"sleepers")).ToString(Formatting.None);
                case "prepare_shelter":
                    return map.GetComponent<ShelterPlanner>().Prepare(Text(a,"id"));
                case "find_build_sites":
                    return ColonyLocation.Sites(map,Text(a,"kind")).ToString(Formatting.None);
                case "find_items":

                    int originX=Number(a,"x"),originZ=Number(a,"z");

                    if(!new IntVec3(originX,0,originZ).InBounds(map)) throw new ArgumentException("Query origin is outside this map.");

                    return ItemQuery.Find(map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)

                        .Where(t=>t.def.category==ThingCategory.Item && !t.Position.Fogged(map)).Select(t=>new ItemRecord {

                            Id=t.thingIDNumber,DefName=t.def.defName,Count=t.stackCount,X=t.Position.x,Z=t.Position.z,Forbidden=t.IsForbidden(Faction.OfPlayer),

                            Category=t.def.IsMedicine?"medicine":t.def.IsNutritionGivingIngestible?"food":t.def.stuffProps!=null?"material":t.def.IsWeapon?"weapon":t.def.IsApparel?"apparel":"other"

                        }),a["defName"]?.Value<string>(),a["category"]?.Value<string>()??"all",a["forbidden"]?.Value<string>()??"any",originX,originZ,

                        a["offset"]==null?0:Number(a,"offset"),a["limit"]==null?20:Number(a,"limit")).ToString(Formatting.None);

                case "allow_item_ids":

                    var ids=a["ids"] as JArray;

                    if(ids==null || ids.Count<1 || ids.Count>40 || ids.Any(id=>id.Type!=JTokenType.Integer)) throw new ArgumentException("Supply 1–40 item IDs from find_items.");

                    var requested=new HashSet<int>(ids.Values<int>());

                    var found=map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).Where(t=>requested.Contains(t.thingIDNumber) && t.def.category==ThingCategory.Item && !t.Position.Fogged(map)).ToList();

                    if(found.Count!=requested.Count) throw new ArgumentException("Some selected items moved off-map or no longer exist/are visible. Query again; nothing changed.");

                    int changed=found.Count(t=>t.IsForbidden(Faction.OfPlayer));

                    foreach(var t in found) t.SetForbidden(false,false);

                    return "Allowed " + changed + " selected item stacks; " + (found.Count-changed) + " already allowed. Normal hauling and reachability rules apply.";

                case "report_blocker":

                    return "Blocked: " + Text(a,"reason");

                case "build_room":

                    return RoomConstruction.Build(map,a);

                case "designate_roof":

                    var roofCells=Area(map,a);

                    if(roofCells.Any(c=>c.Fogged(map))) throw new ArgumentException("Cannot designate a roof in fogged cells.");

                    foreach(var c in roofCells) map.areaManager.BuildRoof[c]=true;

                    return "Designated " + roofCells.Count + " cells for roofing. Builders need normal roof supports and access. A designation is not a completed roof.";

                case "allow_items":

                    var selected = Area(map,a).SelectMany(c=>c.GetThingList(map)).Distinct()

                        .Where(t=>t.def.category==ThingCategory.Item && t.IsForbidden(Faction.OfPlayer) && !t.Position.Fogged(map)).ToList();

                    foreach(var item in selected) item.SetForbidden(false, false);

                    return "Allowed " + selected.Count + " item stacks in the selected area. Normal hauling, access and work rules still apply.";

                case "ensure_stockpile":

                    var existing = map.zoneManager.AllZones.OfType<Zone_Stockpile>().FirstOrDefault();

                    if (existing != null) return "Existing shared stockpile: " + existing.label + ", cells=" + existing.Cells.Count + ". No new zone created.";

                    var cells = Area(map,a);
                    ColonyLocation.Validate(map,Number(a,"x"),Number(a,"z"),Number(a,"width"),Number(a,"height"));

                    if (cells.Any(c=>!c.Walkable(map) || c.GetEdifice(map)!=null || map.zoneManager.ZoneAt(c)!=null))

                        throw new ArgumentException("Area is blocked or already zoned. Inspect another area; nothing changed.");

                    var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile,map.zoneManager);

                    map.zoneManager.RegisterZone(zone);

                    foreach(var cell in cells) zone.AddCell(cell);

                    return "Created shared stockpile: " + zone.label + ", cells=" + cells.Count + ". RimWorld assigns hauling normally.";

                case "list_buildables":

                    string query = Text(a,"search");

                    if(query.Trim().Equals("roof",StringComparison.OrdinalIgnoreCase) || query.Trim().Equals("roofing",StringComparison.OrdinalIgnoreCase))

                        throw new ArgumentException("Roofs are area designations. Use designate_roof, not a building or conduit.");

                    return new JArray(DefDatabase<ThingDef>.AllDefs.Where(d=>d.designationCategory!=null && d.category==ThingCategory.Building &&

                        (d.researchPrerequisites==null || d.researchPrerequisites.All(r=>r.IsFinished)) &&

                        (d.defName.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0 || d.label.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0))

                        .OrderBy(d=>d.defName).Take(15).Select(d=>new JObject { ["defName"]=d.defName,["label"]=d.label,

                            ["sizeX"]=d.size.x,["sizeZ"]=d.size.z,["requiresMaterial"]=d.MadeFromStuff,["materials"]=Materials(map,d) })).ToString(Formatting.None);

                case "place_blueprint":

                    return Build(map,a);

                case "inspect_work_orders":

                    return ColonyObserver.Orders(map).ToString(Formatting.None);

                case "inspect_colonist":

                    var pawn = Pawn(map,a);

                    return new JObject { ["name"]=pawn.LabelShort,

                        ["skills"]=new JArray(pawn.skills.skills.Select(s=>new JObject { ["skill"]=s.def.defName,["level"]=s.Level })),

                        ["work"]=new JArray(DefDatabase<WorkTypeDef>.AllDefs.Select(w=>new JObject { ["workType"]=w.defName,

                            ["disabled"]=pawn.WorkTypeIsDisabled(w),["priority"]=pawn.workSettings?.GetPriority(w) ?? 0 })) }.ToString(Formatting.None);

                case "set_work_priority":

                    var worker = Pawn(map,a);

                    var work = DefDatabase<WorkTypeDef>.GetNamedSilentFail(Text(a,"workType"));

                    int priority = Number(a,"priority");

                    if (priority<0 || priority>4 || work==null || worker.workSettings==null || worker.WorkTypeIsDisabled(work))

                        throw new ArgumentException("Invalid priority/work type, or colonist is incapable. Nothing changed.");

                    if (!Find.PlaySettings.useWorkPriorities) throw new ArgumentException("Player must enable manual work priorities in the Work tab first.");

                    worker.workSettings.SetPriority(work,priority);

                    return worker.LabelShort + ": " + work.defName + " priority=" + priority;

                case "set_plan":

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

        private static JArray Materials(Map map, ThingDef building)

        {

            if(!building.MadeFromStuff) return new JArray();

            var stocks=map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)

                .Where(t=>t.def.category==ThingCategory.Item && !t.Position.Fogged(map)).ToList();

            return new JArray(DefDatabase<ThingDef>.AllDefs.Where(d=>d.stuffProps!=null && building.stuffCategories!=null &&

                building.stuffCategories.Any(c=>d.stuffProps.categories.Contains(c)))

                .Select(d=>new { Def=d, Allowed=stocks.Where(t=>t.def==d && !t.IsForbidden(Faction.OfPlayer)).Sum(t=>t.stackCount),

                    Forbidden=stocks.Where(t=>t.def==d && t.IsForbidden(Faction.OfPlayer)).Sum(t=>t.stackCount) })

                .OrderByDescending(s=>s.Allowed).ThenByDescending(s=>s.Forbidden).ThenBy(s=>s.Def.defName).Take(12)

                .Select(s=>new JObject { ["material"]=s.Def.defName,["label"]=s.Def.label,["allowed"]=s.Allowed,["forbidden"]=s.Forbidden }));

        }

        private static string Build(Map map,JObject a)

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

                if(stuff?.stuffProps==null || def.stuffCategories==null || !def.stuffCategories.Any(c=>stuff.stuffProps.categories.Contains(c)))

                    throw new ArgumentException("Material is required for " + def.defName + ". Choose a material from these compatible options: " + Materials(map,def).ToString(Formatting.None) + ". Nothing changed.");

            }

            ColonyLocation.Validate(map,cell.x,cell.z,def.size.x,def.size.z);
            var rot=new Rot4(rotation);

            var report=GenConstruct.CanPlaceBlueprintAt(def,cell,rot,map);

            if(!report.Accepted) throw new ArgumentException(report.Reason ?? "Cannot build at this cell.");

            GenConstruct.PlaceBlueprintForBuild(def,cell,map,rot,Faction.OfPlayer,stuff);

            return "Construction ordered: " + def.defName + " at " + cell + ". Wait for normal construction work.";

        }



        public static JObject Snapshot(Map map)

        {

            var pawns=map.mapPawns.FreeColonistsSpawned;

            // Indexed item list, not a cell-by-cell map scan. Sampled at most every 10 real seconds.

            var items=map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver);

            var stocks=items.Where(t=>t.def.category==ThingCategory.Item && !t.Position.Fogged(map)).GroupBy(t=>t.def.defName)

                .Select(g=>new { name=g.Key,count=g.Sum(t=>t.stackCount),forbidden=g.Where(t=>t.IsForbidden(Faction.OfPlayer)).Sum(t=>t.stackCount) }).OrderByDescending(x=>x.count).Take(20);

            return new JObject {

                ["shelterProject"]=map.GetComponent<ShelterPlanner>().Active(),["base"]=ColonyLocation.Describe(map),["mapId"]=map.uniqueID,["mapWidth"]=map.Size.x,["mapHeight"]=map.Size.z,

                ["colonistCount"]=pawns.Count,["colonistsTruncated"]=pawns.Count>30,

                ["colonists"]=new JArray(pawns.Take(30).Select(p=>new JObject { ["id"]=p.thingIDNumber,["name"]=p.LabelShort,

                    ["x"]=p.Position.x,["z"]=p.Position.z,["downed"]=p.Downed,["drafted"]=p.Drafted,["weapon"]=p.equipment?.Primary?.def.defName??"none",["canFight"]=!p.WorkTagIsDisabled(WorkTags.Violent) })),

                ["hostiles"]=map.mapPawns.AllPawnsSpawned.Count(p=>p.HostileTo(Faction.OfPlayer)),

                ["looseItemsTop20"]=new JArray(stocks.Select(s=>new JObject { ["defName"]=s.name,["count"]=s.count,["forbidden"]=s.forbidden,["allowed"]=s.count-s.forbidden })),

                ["forbiddenItemsSample"]=new JArray(items.Where(t=>t.def.category==ThingCategory.Item && !t.Position.Fogged(map) && t.IsForbidden(Faction.OfPlayer)).Take(12).Select(t=>new JObject { ["defName"]=t.def.defName,["count"]=t.stackCount,["x"]=t.Position.x,["z"]=t.Position.z })),

                ["stockpileCount"]=map.zoneManager.AllZones.OfType<Zone_Stockpile>().Count(),

                ["stockpiles"]=new JArray(map.zoneManager.AllZones.OfType<Zone_Stockpile>().Take(10).Select(z=>new JObject

                    { ["name"]=z.label,["cells"]=z.Cells.Count,["x"]=z.Cells.FirstOrDefault().x,["z"]=z.Cells.FirstOrDefault().z })),

                ["blueprints"]=map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count,

                ["frames"]=map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count,

                ["activeResearch"]=Find.ResearchManager.GetProject()?.defName ?? "none",

                ["note"]="Forbidden items cannot be used or hauled until allowed. Allowed counts do not guarantee reachability. Existing stockpiles are real zones, not proposals. activeResearch is only the current project, not unlocked technology. Pawns execute normal game jobs."

            };

        }

        public static string Fingerprint(JObject snapshot)

        {

            var copy=(JObject)snapshot.DeepClone();

            copy.Remove("objectives"); // Objective state transitions are fingerprinted separately.

            foreach(var pawn in (JArray)copy["colonists"]) { ((JObject)pawn).Remove("x"); ((JObject)pawn).Remove("z"); }

            // Resource buckets avoid a model request for each individual consumed item.

            foreach(var stock in (JArray)copy["looseItemsTop20"]) stock["count"]=stock["count"].Value<int>()/25;

            return copy.ToString(Formatting.None);

        }

    }

}
