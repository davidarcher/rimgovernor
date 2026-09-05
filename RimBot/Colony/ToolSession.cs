using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimBot.Tools;
namespace RimBot.Colony
{
    // Keep the full API discoverable without attaching every schema to every request.
    public sealed class ToolSession
    {
        private readonly List<string> enabled=new List<string>();
        private static readonly string[] Core={"tools_search","tools_enable","manager_save_plan","manager_report_blocker"};
        public List<ToolDefinition> Definitions() => ToolCatalog.Definitions().Where(t=>Core.Contains(t.Name)||enabled.Contains(t.Name)).ToList();
        public static JArray Discover(string search)
        {
            var words=(search??"").Split(new[]{' ','_'},StringSplitOptions.RemoveEmptyEntries);
            return new JArray(ToolCatalog.Definitions().Where(t=>!Core.Contains(t.Name) && (words.Length==0 || words.Any(w=>(t.Name+" "+t.Description).IndexOf(w,StringComparison.OrdinalIgnoreCase)>=0)))
                .Select(t=>new JObject{["name"]=t.Name,["description"]=ActivitySummary.Short(t.Description,140)}));
        }
        public string Enable(JArray names)
        {
            if(names==null || names.Count<1 || names.Count>6 || names.Any(n=>n.Type!=JTokenType.String)) throw new ArgumentException("Choose 1–6 tool names from tools_search.");
            var available=ToolCatalog.Definitions().Select(t=>t.Name).ToList();
            if(names.Values<string>().Any(n=>!available.Contains(n))) throw new ArgumentException("Unknown tool; discover available names first.");
            foreach(string name in names.Values<string>()) { if(Core.Contains(name)) continue; enabled.Remove(name); enabled.Add(name); }
            while(enabled.Count>6) enabled.RemoveAt(0);
            return "Available next: "+string.Join(", ",enabled)+". Older tools can be enabled again when needed.";
        }
        public bool Available(string name)=>Definitions().Any(t=>t.Name==name);
    }
}
