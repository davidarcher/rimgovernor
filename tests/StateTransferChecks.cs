using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimBot.Colony;
using RimBot.Tools;
public static class StateTransferChecks
{
    public static void Run(Action<bool,string> check)
    {
        var immediate=new TaskLedger();
        foreach(string name in new[]{"orders_allow","orders_allow_all"}) {
            var allow=new ToolCall{Name=name,Arguments=new JObject()};
            immediate.Record(1,100,allow,"Allowed",true);
            check(immediate.Rows.Last.Value<string>("state")=="Complete","Allow left waiting for completion");
            immediate.Rows.Last["state"]="Issued";
        }
        var migrated=new TaskLedger(); migrated.Load(immediate.Serialize());
        check(migrated.Rows.All(t=>t.Value<string>("state")=="Complete"),"Saved Allow entries stayed pending");
        var failedAllow=new TaskLedger(); failedAllow.Record(1,100,new ToolCall{Name="orders_allow",Arguments=new JObject()},"Missing items",false);
        check(failedAllow.Rows.Count==0,"Failed Allow recorded as complete");
        var ledger=new TaskLedger();
        var build=new ToolCall{Name="architect_build",Arguments=new JObject{["defName"]="Bed",["x"]=10,["z"]=11,["rotation"]=0,["projectId"]="sleep"}};
        ledger.Record(1,100,build,"Ordered",true);
        var record=(JObject)ledger.Rows[0];
        check(record.Value<string>("state")=="Issued","Accepted tool incorrectly completes task");
        ledger.ObserveConstruction(record,200,"blueprint",0);
        check(record.Value<string>("state")=="Ordered","Blueprint treated as productive work");
        ledger.Record(1,7000,build,"Already exists",true);
        ledger.ObserveConstruction(record,7600,"blueprint",0);
        check(record.Value<string>("state")=="Stalled","Repeated designation resets stall clock");
        ledger.ObserveConstruction(record,7700,"frame",5);
        check(record.Value<string>("state")=="In progress","Frame progress did not clear stall");
        ledger.ObserveConstruction(record,8000,"missing",0);
        check(record.Value<string>("state")=="Missing","Disappeared order falsely completed");
        ledger.ObserveConstruction(record,8100,"built",0);
        check(record.Value<string>("state")=="Complete","Observed building not completed");
        var restored=new TaskLedger(); restored.Load(ledger.Serialize());
        check(restored.Serialize()==ledger.Serialize(),"Tracked work save round-trip failed");
        check(restored.View(2).Count==0,"Other map's tasks leaked into context");
        check(restored.View(1)[0]["projectId"].Value<string>()=="sleep","Project link lost");
        ledger.Record(2,100,build,"No path",false);
        check(ledger.View(2).Count==0 && ledger.Rows.Count==1,"Rejected API call became tracked colony work");
        var state=JObject.Parse(@"{mapId:0,mapWidth:250,mapHeight:250,base:{x:100,z:100,note:'Inspect before building'},
            colonistCount:1,colonistsTruncated:false,colonists:[{id:239,name:'Fernanda',x:102,z:99,canFight:false,downed:false,weapon:'none'}],
            looseItemsTop20:[{defName:'Steel',count:120,forbidden:120,allowed:0}],
            forbiddenItemsSample:[{id:3844,defName:'Steel',count:120,x:100,z:101}],
            stockpileCount:0,stockpiles:[],blueprints:1,frames:0,buildings:[{defName:'Bed',built:0,pending:1}],
            visibleHostileFactionPawns:{total:1,nextOffset:null,pawns:[{id:998,name:'Megascarab',job:'Wait_Wander',hostileToPlayer:true}]},
            hostilityNote:'Faction relationship alone does not imply attack or block construction.',
            objectives:[{id:'safety',state:'Satisfied',priority:5,evidence:'No unarmed capable fighters.',next:'Check equipment before assigning weapons.'}],
            notifications:{alertsAvailable:false,liveMessagesAvailable:true,alerts:[],letters:[],messages:[{text:'Need batteries',tick:45}],scope:'Across maps'},
            note:'Forbidden stacks must be allowed. Existing zones are not proposals.'}");
        string original=state.ToString(Formatting.None);
        var compact=StateTransfer.Colony(state);
        var strategic=StateTransfer.Colony(state,true);
        check(state.ToString(Formatting.None)==original,"Transport mutated game snapshot");
        check(compact["forbiddenSupplyControls"]["selectedStacks"].Value<string>()=="orders_allow" && compact["forbiddenSupplyControls"]["mapWide"]==null,"Forbidden supply state lacks actionable control");
        check(compact["colonists"][0]["canFight"].Value<bool>()==false,"Transport lost violence restriction");
        check(compact["looseItemsTop20"][0]["allowed"].Value<int>()==0 && compact["looseItemsTop20"][0]["totalOnMap"].Value<int>()==120,"Total supplies confused with usable supplies");
        check(compact["looseItemsTop20"][0]["forbidden"].Value<int>()==120,"Transport lost forbidden supplies");
        check(JToken.DeepEquals(compact["buildings"],state["buildings"]),"Transport lost unfinished orders");
        check(compact["notifications"]["alertsAvailable"].Value<bool>()==false,"Unavailable notifications became empty success");
        check(strategic["colonists"][0]["canFight"].Value<bool>()==false && strategic["colonists"][0]["x"]==null,"Strategy must retain capability without tactical coordinates");
        check(strategic["stockpileCount"].Value<int>()==0,"Strategy lost storage count");
        check(StateTransfer.Changes(compact,compact).Count==0,"Unchanged state was resent");
        var changed=(JObject)compact.DeepClone(); changed["buildings"]=new JArray(); changed["colonists"][0]["downed"]=true;
        var delta=StateTransfer.Changes(compact,changed);
        check(delta.Count==2 && delta["buildings"].Count()==0,"Delta lost emptied lists or added unchanged fields");
        var reconstructed=(JObject)compact.DeepClone(); foreach(var field in delta.Properties()) reconstructed[field.Name]=field.Value.DeepClone();
        check(JToken.DeepEquals(reconstructed,changed),"Delta reconstruction differs from fresh state");
        check(JToken.DeepEquals(StateTransfer.Changes(null,compact),compact),"First transfer lacks full state");
        var pawn=JObject.Parse(@"{pawn:{id:239,name:'Fernanda',kind:'Colonist',faction:'Colony',colonist:true,prisoner:false,animal:false,x:3,z:4,mood:0.4,canFight:false,canTakeOrder:true,weapon:null},section:'health',conditions:[{defName:'Cut',severity:0.0001}],pages:{conditions:{total:3,nextOffset:1}}}");
        var payload=JObject.Parse(StateTransfer.ToolResult("pawns_inspect",pawn.ToString()));
        check(payload["pawn"]["canFight"].Value<bool>()==false && payload["pawn"]["mood"].Value<double>()==0.4,"Pawn result lost restriction or mood");
        check(JToken.DeepEquals(payload["conditions"],pawn["conditions"]),"Health details rounded or discarded");
        check(JToken.DeepEquals(payload["pages"],pawn["pages"]),"Tool transport lost pagination");
        check(StateTransfer.ToolResult("equipment_equip","Incapable of violence.")=="Incapable of violence.","Failure reason changed");
        var orders=new OrderMemory(); var args=new JObject{["pawnId"]=239,["itemId"]=3844};
        orders.Remember(new ToolCall{Name="equipment_equip",Arguments=args},"Incapable of violence.",false);
        args["itemId"]=1;
        var memory=JArray.Parse(orders.Serialize());
        check(memory[0]["args"]["itemId"].Value<int>()==3844 && !memory[0]["accepted"].Value<bool>(),"Order memory lost exact failed arguments");
        check(memory[0]["result"].Value<string>()=="Incapable of violence.","Order memory lost rejection reason");
        orders.Remember(new ToolCall{Name="equipment_equip",Arguments=new JObject{["pawnId"]=239,["itemId"]=3844}},"Incapable of violence.",false);
        check(JArray.Parse(orders.Serialize()).Count==1,"Repeated failures crowd out other attempts");
        orders.Clear(); check(orders.Serialize()=="[]","Attempts leaked into another review");

        Console.WriteLine("State fixture chars: original="+original.Length+", execution="+compact.ToString(Formatting.None).Length+", strategy="+strategic.ToString(Formatting.None).Length+", unchanged follow-up="+StateTransfer.Changes(compact,compact).ToString(Formatting.None).Length+". Character counts, not model tokens.");
    }
}
