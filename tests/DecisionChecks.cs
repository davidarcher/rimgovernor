using System;
using Newtonsoft.Json.Linq;
using RimBot.Colony;
using RimBot.Tools;
internal static class DecisionChecks
{
    public static void Run(Action<bool,string> check)
    {
        var identifiers=new ObservedIdentifiers();
        identifiers.Remember("construction_list","[{id:42,defName:'Bed',stage:'blueprint'},{id:43,defName:'Bed',stage:'frame'}]");
        identifiers.Remember("zones_list","[{id:42,name:'Stockpile'}]");
        identifiers.Remember("selection_inspect","{menu:{orders:[{actionId:'old-action',label:'Construct'}]}}");
        identifiers.RemoveMissingThings(new System.Collections.Generic.HashSet<int>{43});
        identifiers.ClearActionHandles();
        var retained=JArray.Parse(identifiers.Serialize());
        check(retained.Count==2,"Stale blueprint/action retained or zone with same integer ID removed");
        check(identifiers.Serialize().Contains("frame") && !identifiers.Serialize().Contains("blueprint"),"Construction stage lost during compaction");
        var memory=new DecisionMemory();
        var call=new ToolCall{Name="items_list",Arguments=new JObject{["defName"]="Gun_BoltActionRifle",["x"]=12,["z"]=20}};
        check(!memory.Observe(call,"{totalStacks:0}",true),"First empty query marked repetitive");
        call.Arguments["x"]=99; memory.Observe(call,"{totalStacks:0}",true);
        var restored=new DecisionMemory(); restored.Load(memory.Serialize());
        call.Arguments["limit"]=40;
        check(restored.Observe(call,"{totalStacks:0}",true),"Empty map query lost across origins/save/compaction");
        check(!restored.Observe(call,"{totalStacks:1,items:[{id:42}]}",true),"Changed results treated as unchanged");
        check(restored.Repeats().Count==0,"Changed observation left stale repetition");
        var big=new ToolCall{Name="map_inspect",Arguments=new JObject{["x"]=1,["z"]=1}};
        restored.Observe(big,new string('x',20000),true);
        check(restored.Serialize().Length<9000,"Large observations crowd out repetition memory");
        var different=new ToolCall{Name="items_list",Arguments=new JObject{["defName"]="WoodLog"}};
        check(!restored.Observe(different,"{totalStacks:0}",true),"Different item search conflated");
        var action=new ToolCall{Name="orders_allow",Arguments=new JObject{["ids"]=new JArray(42)}};
        restored.Observe(action,"already allowed",true); restored.Observe(action,"already allowed",true);
        check(restored.Observe(action,"already allowed",true),"No-op repetition missed");
    }
}
