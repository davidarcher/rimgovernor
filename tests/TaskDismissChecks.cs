using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimBot.Colony;
using RimBot.Tools;
public static class TaskDismissChecks
{
    public static void Run(Action<bool,string> check)
    {
        var ledger=new TaskLedger();
        var steel=new ToolCall{Name="architect_build",Arguments=new JObject{["defName"]="Wall",["material"]="Steel",["x"]=4,["z"]=5,["rotation"]=0}};
        ledger.Record(1,10,steel,"Ordered",true);
        var original=(JObject)ledger.Rows[0];
        ledger.ObserveConstruction(original,20,"built",0);
        ledger.ObserveConstruction(original,30,"missing",0);
        check(original.Value<string>("state")=="Complete" && !TaskLedger.IsActive(original),"Deconstruction revived completed work");
        var wood=new ToolCall{Name="architect_build",Arguments=(JObject)steel.Arguments.DeepClone()}; wood.Arguments["material"]="WoodLog";
        ledger.Record(1,40,wood,"Ordered",true);
        check(ledger.Rows.Count==2 && ledger.Rows.Count(TaskLedger.IsActive)==1,"Replacement inherited completed work");
        var replacement=(JObject)ledger.Rows[1];
        ledger.ObserveConstruction(replacement,50,"missing",0);
        check(replacement.Value<string>("state")=="Removed" && !TaskLedger.IsActive(replacement),"Cancelled construction remained active");
        ledger.Record(1,60,wood,"Ordered",true);
        check(ledger.Rows.Count==3 && ledger.Rows.Last.Value<int>("lastProgressTick")==60,"Reissued construction inherited obsolete progress");
        string id=ledger.Rows.Last.Value<string>("id");
        check(!ledger.Dismiss(2,id) && ledger.Rows.Count==3,"Dismiss crossed map boundary");
        check(ledger.Dismiss(1,id) && ledger.Rows.Count==2 && !ledger.Dismiss(1,id),"Dismiss did not remove only selected record");
        var restored=new TaskLedger(); restored.Load(ledger.Serialize());
        check(restored.Serialize()==ledger.Serialize(),"Dismiss did not survive save/load");
        restored.Load("[{id:'legacy',mapId:1,state:'Missing'}]");
        check(restored.Rows[0].Value<string>("state")=="Removed","Legacy missing work was revived");
    }
}
