using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
namespace RimBot.Colony
{
    public static class ArchitectCatalog
    {
        public static JArray Read(string category)=>new JArray(DefDatabase<DesignationCategoryDef>.AllDefs
            .Where(d=>category==null || d.defName==category).OrderBy(d=>d.order).Select(d=>new JObject {
                ["defName"]=d.defName,["label"]=d.label,
                ["controls"]=new JArray(d.ResolvedAllowedDesignators.Where(c=>c.Visible).Select(c=>new JObject {
                    ["label"]=c.Label,["type"]=c.GetType().Name,["adapter"]=Adapter(c)
                }))
            }));
        private static string Adapter(Designator d)
        {
            if(d is Designator_Build) return "architect_build";
            if(d is Designator_Unforbid) return "orders_allow";
            if(d is Designator_Hunt) return "orders_hunt";
            if(d is Designator_AreaBuildRoof) return "areas_build_roof";
            if(d is Designator_ZoneAdd_Growing) return "zones_growing_designate";
            if(d is Designator_ZoneAddStockpile_Resources) return "zones_stockpile_designate";
            if(d is Designator_ZoneDelete) return "zones_remove_cells";
            return null;
        }
    }
}
