using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimBot.Tools;
namespace RimBot.Colony
{
    // Stable full catalog; native menu handles are data, never a rotating tool window.
    public sealed class ToolSession
    {
        private readonly List<string> actionIds=new List<string>();
        public void ClearActions()=>actionIds.Clear();
        public void ObserveMenu(string result)
        {
            actionIds.Clear();
            JToken parsed; try { parsed=JToken.Parse(result); } catch { return; }
            if(!(parsed is JContainer container)) return;
            actionIds.AddRange(container.Descendants().OfType<JObject>().Where(o=>o["actionId"]?.Type==JTokenType.String && o.Value<bool?>("enabled")==true).Select(o=>o.Value<string>("actionId")).Distinct());
        }
        public List<ToolDefinition> Definitions()
        {
            var definitions=ToolCatalog.Definitions();
            var action=definitions.FirstOrDefault(t=>t.Name=="pawns_order");
            if(action!=null && actionIds.Count>0) { var schema=JObject.Parse(action.ParametersJson); schema["properties"]["actionId"]["enum"]=new JArray(actionIds); action.ParametersJson=schema.ToString(Newtonsoft.Json.Formatting.None); }
            return definitions;
        }
        public static JArray Discover(string search)
        {
            var exact=ToolCatalog.Definitions().FirstOrDefault(t=>t.Name.Equals(ToolNames.Canonical(search?.Trim()),StringComparison.OrdinalIgnoreCase));
            if(exact!=null) return new JArray(new JObject{["name"]=exact.Name,["description"]=exact.Description});
            var words=(search??"").Split(new[]{' ','_'},StringSplitOptions.RemoveEmptyEntries);
            var candidates=ToolCatalog.Definitions();
            if(words.Length==0) return new JArray(candidates.Select(t=>t.Name));
            return new JArray(candidates.Select(t=>new { Tool=t,Score=words.Sum(w=>
                t.Name.IndexOf(w,StringComparison.OrdinalIgnoreCase)>=0?4:
                t.Description.IndexOf(w,StringComparison.OrdinalIgnoreCase)>=0?1:0) })
                .Where(t=>t.Score>0).OrderByDescending(t=>t.Score).ThenBy(t=>t.Tool.Name)
                .Select(t=>new JObject{["name"]=t.Tool.Name,["description"]=ActivitySummary.Short(t.Tool.Description,100)}));
        }
        // Compatibility for old conversation/test clients; never changes the schema set.
        public string Enable(JArray names)
        {
            if(names==null || names.Any(n=>n.Type!=JTokenType.String || !Available(ToolNames.Canonical(n.Value<string>()))))
                throw new ArgumentException("Unknown tool. Use an available tool name.");
            return "All tools are already available; no enable step is needed.";
        }
        public bool Available(string name)=>Definitions().Any(t=>t.Name==name);
    }
}
