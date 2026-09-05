using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimBot.Colony
{
    // Read-only adapters. RimWorld remains the source of every reported value.
    public static class PawnQueries
    {
        private static Pawn Resolve(Map map, int id) => map.mapPawns.AllPawnsSpawned
            .FirstOrDefault(p => p.thingIDNumber == id && !p.Position.Fogged(map))
            ?? throw new ArgumentException("Pawn is no longer visible on this map. Query again.");

        private static JObject Summary(Pawn p) => new JObject {
            ["id"]=p.thingIDNumber,["name"]=p.LabelShort,["kind"]=p.kindDef.defName,
            ["faction"]=p.Faction?.Name,["colonist"]=p.IsColonist,["prisoner"]=p.IsPrisonerOfColony,
            ["animal"]=p.RaceProps.Animal,["x"]=p.Position.x,["z"]=p.Position.z,
            ["downed"]=p.Downed,["drafted"]=p.Drafted,["job"]=p.CurJob?.def.defName,
            ["mood"]=p.needs?.mood?.CurLevelPercentage,["weapon"]=p.equipment?.Primary?.def.defName
        };

        public static JObject Find(Map map, JObject a)
        {
            string group=a.Value<string>("group") ?? "all", kind=a.Value<string>("kind");
            int offset=a.Value<int?>("offset") ?? 0, limit=a.Value<int?>("limit") ?? 20;
            if(offset<0 || limit<1 || limit>40) throw new ArgumentException("offset must be nonnegative; limit must be 1–40.");
            if(!new[]{"all","colonists","prisoners","animals","colony_animals","wild_animals"}.Contains(group))
                throw new ArgumentException("Unknown pawn group.");
            var pawns=map.mapPawns.AllPawnsSpawned.Where(p=>!p.Position.Fogged(map));
            if(group=="colonists") pawns=pawns.Where(p=>p.IsColonist);
            if(group=="prisoners") pawns=pawns.Where(p=>p.IsPrisonerOfColony);
            if(group=="animals") pawns=pawns.Where(p=>p.RaceProps.Animal);
            if(group=="colony_animals") pawns=pawns.Where(p=>p.RaceProps.Animal && p.Faction==Faction.OfPlayer);
            if(group=="wild_animals") pawns=pawns.Where(p=>p.RaceProps.Animal && p.Faction==null);
            if(!string.IsNullOrWhiteSpace(kind)) pawns=pawns.Where(p=>p.kindDef.defName==kind);
            bool hasOrigin=a["x"]!=null || a["z"]!=null;
            if(hasOrigin && (a["x"]==null || a["z"]==null)) throw new ArgumentException("Supply both x and z.");
            int x=a.Value<int?>("x")??0,z=a.Value<int?>("z")??0;
            if(hasOrigin && !new IntVec3(x,0,z).InBounds(map)) throw new ArgumentException("Origin is outside this map.");
            var ordered=hasOrigin ? pawns.OrderBy(p=>p.Position.DistanceToSquared(new IntVec3(x,0,z))).ThenBy(p=>p.thingIDNumber)
                : pawns.OrderBy(p=>p.thingIDNumber).ThenBy(p=>p.thingIDNumber);
            var all=ordered.ToList();
            return new JObject { ["total"]=all.Count,["offset"]=offset,
                ["nextOffset"]=offset+limit<all.Count?(JToken)(offset+limit):JValue.CreateNull(),
                ["pawns"]=new JArray(all.Skip(offset).Take(limit).Select(Summary)) };
        }

        public static JObject Inspect(Map map, int id, string section, int offset=0, int limit=10)
        {
            if(offset<0 || limit<1 || limit>20) throw new ArgumentException("offset must be nonnegative; limit must be 1–20.");
            var p=Resolve(map,id);
            var result=new JObject { ["pawn"]=Summary(p),["section"]=section };
            switch(section)
            {
                case "needs":
                    result["needs"]=new JArray(p.needs?.AllNeeds.Select(n=>new JObject {
                        ["defName"]=n.def.defName,["label"]=n.LabelCap.ToString(),["level"]=n.CurLevelPercentage
                    }) ?? Enumerable.Empty<JObject>());
                    break;
                case "social":
                    result["traits"]=new JArray(p.story?.traits?.allTraits.Select(t=>new JObject {
                        ["defName"]=t.def.defName,["degree"]=t.Degree,["label"]=t.Label
                    }) ?? Enumerable.Empty<JObject>());
                    result["relations"]=new JArray(p.relations?.DirectRelations.Select(r=>new JObject {
                        ["relation"]=r.def.defName,["pawnId"]=r.otherPawn.thingIDNumber,["name"]=r.otherPawn.LabelShort,
                        ["opinion"]=p.relations.OpinionOf(r.otherPawn)
                    }) ?? Enumerable.Empty<JObject>());
                    break;
                case "mood":
                    if(p.needs?.mood==null) { result["available"]=false; break; }
                    var thoughts=new System.Collections.Generic.List<Thought>();
                    p.needs.mood.thoughts.GetAllMoodThoughts(thoughts);
                    result["thoughts"]=new JArray(thoughts.Select(t=>new JObject {
                        ["defName"]=t.def.defName,["label"]=t.LabelCap.ToString(),["moodOffset"]=t.MoodOffset()
                    }));
                    result["note"]="Individual active thoughts; final mood and stacking are computed by RimWorld.";
                    break;
                case "health":
                    result["conditions"]=new JArray(p.health.hediffSet.hediffs.Where(h=>h.Visible).Select(h=>new JObject {
                        ["defName"]=h.def.defName,["label"]=h.Label,["part"]=h.Part?.Label,
                        ["severity"]=h.Severity,["tendable"]=h.TendableNow()
                    }));
                    result["capacities"]=new JArray(DefDatabase<PawnCapacityDef>.AllDefs.Select(c=>new JObject {
                        ["defName"]=c.defName,["level"]=p.health.capacities.GetLevel(c)
                    }));
                    break;
                case "equipment":
                    result["equipment"]=new JArray(p.equipment?.AllEquipmentListForReading.Select(t=>new JObject {
                        ["id"]=t.thingIDNumber,["defName"]=t.def.defName,["label"]=t.Label
                    }) ?? Enumerable.Empty<JObject>());
                    result["apparel"]=new JArray(p.apparel?.WornApparel.Select(t=>new JObject {
                        ["id"]=t.thingIDNumber,["defName"]=t.def.defName,["label"]=t.Label
                    }) ?? Enumerable.Empty<JObject>());
                    break;
                case "work":
                    result["skills"]=new JArray(p.skills?.skills.Select(s=>new JObject {
                        ["skill"]=s.def.defName,["level"]=s.Level,["passion"]=s.passion.ToString(),["disabled"]=s.TotallyDisabled
                    }) ?? Enumerable.Empty<JObject>());
                    result["work"]=new JArray(DefDatabase<WorkTypeDef>.AllDefs.Select(w=>new JObject {
                        ["workType"]=w.defName,["disabled"]=p.WorkTypeIsDisabled(w),["priority"]=p.workSettings?.GetPriority(w)??0
                    }));
                    break;
                case "animal":
                    if(!p.RaceProps.Animal) throw new ArgumentException("Pawn is not an animal.");
                    result["bodySize"]=p.BodySize; result["predator"]=p.RaceProps.predator; result["huntingDesignated"]=map.designationManager.DesignationOn(p,DesignationDefOf.Hunt)!=null;
                    result["gender"]=p.gender.ToString();
                    result["ageYears"]=p.ageTracker.AgeBiologicalYearsFloat;
                    result["lifeStage"]=p.ageTracker.CurLifeStage.defName;
                    result["training"]=new JArray(p.training==null?Enumerable.Empty<JObject>():DefDatabase<TrainableDef>.AllDefs.Select(t=>new JObject {
                        ["defName"]=t.defName,["learned"]=p.training.HasLearned(t),["wanted"]=p.training.GetWanted(t)
                    }));
                    break;
                default: throw new ArgumentException("Unknown detail section. Use needs, mood, social, health, equipment, work or animal.");
            }
            var pages=new JObject();
            foreach(var property in result.Properties().ToList()) {
                var array=property.Value as JArray;
                if(array==null) continue;
                pages[property.Name]=new JObject { ["total"]=array.Count,["offset"]=offset,
                    ["nextOffset"]=offset+limit<array.Count?(JToken)(offset+limit):JValue.CreateNull() };
                property.Value=new JArray(array.Skip(offset).Take(limit));
            }
            result["pages"]=pages;
            return result;
        }
    }
}
