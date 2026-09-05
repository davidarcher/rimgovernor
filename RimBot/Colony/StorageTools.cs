using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
namespace RimBot.Colony
{
    public static class StorageTools
    {
        private static IStoreSettingsParent Resolve(Map map,JObject a)
        {
            if((a["zoneId"]==null)==(a["thingId"]==null)) throw new ArgumentException("Supply exactly one zoneId or thingId. Call storage_inspect with no target to list actual storage IDs.");
            IStoreSettingsParent parent;
            if(a["zoneId"]!=null) parent=map.zoneManager.AllZones.OfType<Zone_Stockpile>().FirstOrDefault(z=>z.ID==a.Value<int>("zoneId"));
            else parent=map.listerBuildings.allBuildingsColonist.FirstOrDefault(t=>t.thingIDNumber==a.Value<int>("thingId") && !t.Position.Fogged(map)) as IStoreSettingsParent;
            if(parent==null || !parent.StorageTabVisible) throw new ArgumentException("No player-configurable storage at that ID. Loose stacks are items, not storage zones. Call storage_inspect with no target to list actual storage IDs.");
            return parent;
        }
        private static JObject Targets(Map map,JObject a)
        {
            int offset=a.Value<int?>("offset")??0,limit=a.Value<int?>("limit")??20;
            if(offset<0 || limit<1 || limit>40) throw new ArgumentException("Use offset >= 0 and limit 1-40.");
            var rows=new List<JObject>();
            foreach(var zone in map.zoneManager.AllZones.OfType<Zone_Stockpile>().OrderBy(z=>z.ID))
                rows.Add(new JObject{["zoneId"]=zone.ID,["label"]=zone.label,["priority"]=zone.GetStoreSettings().Priority.ToString()});
            foreach(var building in map.listerBuildings.allBuildingsColonist.OrderBy(b=>b.thingIDNumber))
                if(!building.Position.Fogged(map) && building is IStoreSettingsParent storage && storage.StorageTabVisible)
                    rows.Add(new JObject{["thingId"]=building.thingIDNumber,["label"]=building.LabelShort,["x"]=building.Position.x,["z"]=building.Position.z});
            return new JObject{["total"]=rows.Count,["storage"]=new JArray(rows.Skip(offset).Take(limit)),
                ["nextOffset"]=offset+limit<rows.Count?(JToken)(offset+limit):JValue.CreateNull(),
                ["note"]=rows.Count==0?"No stockpile zones or configurable storage buildings exist. Loose stacks are items; query items_list and assess specific supplies before allowing selected stacks.":"Copy zoneId or thingId into storage_inspect/configure. These are storage settings, not loose item stacks."};
        }
        public static JObject Inspect(Map map,JObject a)
        {
            if(a["zoneId"]==null && a["thingId"]==null) return Targets(map,a);
            var parent=Resolve(map,a); var settings=parent.GetStoreSettings(); var filter=settings.filter;
            int offset=a.Value<int?>("offset")??0,limit=a.Value<int?>("limit")??20;
            if(offset<0 || limit<1 || limit>40) throw new ArgumentException("Use offset >= 0 and limit 1–40.");
            var allowed=filter.AllowedThingDefs.Where(d=>settings.AllowedToAccept(d)).OrderBy(d=>d.defName).ToList();
            return new JObject {
                ["priority"]=settings.Priority.ToString(),["priorities"]=new JArray(Enum.GetNames(typeof(StoragePriority)).Where(n=>n!="Unstored")),
                ["summary"]=filter.Summary,["allowedTotal"]=allowed.Count,["nextOffset"]=offset+limit<allowed.Count?(JToken)(offset+limit):JValue.CreateNull(),
                ["allowed"]=new JArray(allowed.Skip(offset).Take(limit).Select(d=>d.defName)),
                ["specialFilters"]=new JArray(DefDatabase<SpecialThingFilterDef>.AllDefs.Where(d=>!filter.hiddenSpecialFilters.Contains(d)).Select(d=>new JObject{["defName"]=d.defName,["label"]=d.label,["allowed"]=filter.Allows(d)})),
                ["qualityMin"]=filter.AllowedQualityLevels.min.ToString(),["qualityMax"]=filter.AllowedQualityLevels.max.ToString(),
                ["hitPointsMin"]=filter.AllowedHitPointsPercents.min,["hitPointsMax"]=filter.AllowedHitPointsPercents.max,
                ["qualityConfigurable"]=filter.allowedQualitiesConfigurable,["hitPointsConfigurable"]=filter.allowedHitPointsConfigurable
            };
        }
        public static JObject Options(JObject a)
        {
            string kind=a.Value<string>("kind")??"thing",search=a.Value<string>("search")??"";
            int offset=a.Value<int?>("offset")??0,limit=a.Value<int?>("limit")??20;
            if(offset<0 || limit<1 || limit>40) throw new ArgumentException("Use offset >= 0 and limit 1–40.");
            IEnumerable<Def> defs;
            switch(kind) {
                case "thing": defs=DefDatabase<ThingDef>.AllDefs.Where(d=>d.EverStorable(false)); break;
                case "category": defs=DefDatabase<ThingCategoryDef>.AllDefs; break;
                case "special": defs=DefDatabase<SpecialThingFilterDef>.AllDefs; break;
                default: throw new ArgumentException("kind must be thing, category or special.");
            }
            var matches=defs.Where(d=>(d.defName+" "+d.label).IndexOf(search,StringComparison.OrdinalIgnoreCase)>=0).OrderBy(d=>d.defName).ToList();
            return new JObject{["total"]=matches.Count,["nextOffset"]=offset+limit<matches.Count?(JToken)(offset+limit):JValue.CreateNull(),
                ["options"]=new JArray(matches.Skip(offset).Take(limit).Select(d=>new JObject{["defName"]=d.defName,["label"]=d.label,["description"]=ActivitySummary.Short(d.description,200)}))};
        }
        public static string Configure(Map map,JObject a)
        {
            var parent=Resolve(map,a); var settings=parent.GetStoreSettings();
            // Validate and build the new filter before touching live settings.
            var candidate=new StorageSettings(); candidate.CopyFrom(settings);
            if(a["copyFrom"] is JObject copy) candidate.CopyFrom(Resolve(map,copy).GetStoreSettings());
            string reset=a.Value<string>("reset")??"unchanged";
            if(reset=="nothing") candidate.filter.SetDisallowAll();
            else if(reset=="everything") candidate.filter.SetAllowAll(parent.GetParentStoreSettings()?.filter);
            else if(reset!="unchanged") throw new ArgumentException("reset must be unchanged, nothing or everything.");
            foreach(var rule in a["rules"] as JArray??new JArray()) {
                if(rule["allow"]?.Type!=JTokenType.Boolean) throw new ArgumentException("Each rule needs allow true/false.");
                bool allow=rule.Value<bool>("allow"); string name=rule.Value<string>("defName"),kind=rule.Value<string>("kind");
                if(string.IsNullOrEmpty(name)) throw new ArgumentException("Use a definition from storage_filter_options.");
                switch(kind) {
                    case "thing":
                        var thing=DefDatabase<ThingDef>.GetNamedSilentFail(name);
                        if(thing==null || !thing.EverStorable(false)) throw new ArgumentException("Unknown storable item: "+name);
                        if(allow && parent.GetParentStoreSettings()?.AllowedToAccept(thing)==false) throw new ArgumentException("This storage cannot accept "+name+" under its fixed game rules.");
                        candidate.filter.SetAllow(thing,allow); break;
                    case "category":
                        var category=DefDatabase<ThingCategoryDef>.GetNamedSilentFail(name);
                        if(category==null) throw new ArgumentException("Unknown item category: "+name);
                        candidate.filter.SetAllow(category,allow); break;
                    case "special":
                        var special=DefDatabase<SpecialThingFilterDef>.GetNamedSilentFail(name);
                        if(special==null || settings.filter.hiddenSpecialFilters.Contains(special)) throw new ArgumentException("Special filter unavailable: "+name);
                        candidate.filter.SetAllow(special,allow); break;
                    default: throw new ArgumentException("Rule kind must be thing, category or special.");
                }
            }
            if(a["priority"]!=null) {
                if(!Enum.TryParse(a.Value<string>("priority"),out StoragePriority priority) || !Enum.IsDefined(typeof(StoragePriority),priority) || priority==StoragePriority.Unstored) throw new ArgumentException("Use a storage priority from storage_inspect.");
                candidate.Priority=priority;
            }
            if(a["qualityMin"]!=null || a["qualityMax"]!=null) {
                if(!settings.filter.allowedQualitiesConfigurable) throw new ArgumentException("Quality filter is not configurable here.");
                if(!Enum.TryParse(a.Value<string>("qualityMin")??candidate.filter.AllowedQualityLevels.min.ToString(),out QualityCategory min) ||
                   !Enum.TryParse(a.Value<string>("qualityMax")??candidate.filter.AllowedQualityLevels.max.ToString(),out QualityCategory max) || !Enum.IsDefined(typeof(QualityCategory),min) || !Enum.IsDefined(typeof(QualityCategory),max) || min>max) throw new ArgumentException("Invalid quality range.");
                candidate.filter.AllowedQualityLevels=new QualityRange(min,max);
            }
            if(a["hitPointsMin"]!=null || a["hitPointsMax"]!=null) {
                if(!settings.filter.allowedHitPointsConfigurable) throw new ArgumentException("Hit point filter is not configurable here.");
                float min=a.Value<float?>("hitPointsMin")??candidate.filter.AllowedHitPointsPercents.min,max=a.Value<float?>("hitPointsMax")??candidate.filter.AllowedHitPointsPercents.max;
                if(float.IsNaN(min) || float.IsNaN(max) || min<0 || max>1 || min>max) throw new ArgumentException("Hit point range must be between 0 and 1.");
                candidate.filter.AllowedHitPointsPercents=new FloatRange(min,max);
            }
            settings.CopyFrom(candidate);
            return "Storage updated: "+settings.Priority+" priority; "+settings.filter.Summary+".";
        }
    }
}
