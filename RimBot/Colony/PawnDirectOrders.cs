using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
namespace RimBot.Colony
{
    public static class PawnDirectOrders
    {
        // Native single-pawn menu providers; no replacement combat or rescue job logic.
        private static Game handleGame;
        private static readonly Dictionary<string,JObject> Handles=new Dictionary<string,JObject>();
        private static readonly Queue<string> HandleOrder=new Queue<string>();
        private static void CheckHandles()
        {
            if(handleGame==Verse.Current.Game) return;
            handleGame=Verse.Current.Game; Handles.Clear(); HandleOrder.Clear();
        }
        private static string SaveHandle(Map map,JObject args,string provider,string label)
        {
            CheckHandles();
            string id="action_"+Guid.NewGuid().ToString("N");
            var saved=(JObject)args.DeepClone(); saved["provider"]=provider; saved["option"]=label; saved["mapId"]=map.uniqueID;
            Handles[id]=saved; HandleOrder.Enqueue(id);
            while(HandleOrder.Count>128) Handles.Remove(HandleOrder.Dequeue());
            return id;
        }
        private static readonly string[] Providers={"NativeMenu"};
        private static Pawn Actor(Map map,int id)
        {
            PlayerOrders.RequireMap(map);
            var pawn=map.mapPawns.AllPawnsSpawned.FirstOrDefault(p=>p.thingIDNumber==id && !p.Position.Fogged(map));
            if(pawn==null || !pawn.CanTakeOrder) throw new ArgumentException("Choose a visible player-controllable pawn from pawns_list.");
            return pawn;
        }
        public static string Toggle(Map map,JObject args,bool fire)
        {
            int id=RequiredInt(args,"pawnId");
            if(args["enabled"]?.Type!=JTokenType.Boolean) throw new ArgumentException("Supply enabled true or false.");
            var pawn=Actor(map,id); bool desired=args.Value<bool>("enabled");
            if(pawn.drafter==null) throw new ArgumentException("This pawn has no drafting controls.");
            var command=pawn.GetGizmos().OfType<Command_Toggle>().FirstOrDefault(c=>fire?c.tutorTag=="FireAtWillToggle":c.hotKey==KeyBindingDefOf.Command_ColonistDraft);
            if(command==null) throw new ArgumentException(fire?"Fire at will is not available for this pawn's current state/equipment.":"Draft control is not available for this pawn.");
            if(command.Disabled) throw new ArgumentException(command.disabledReason??"The native command is disabled.");
            if(command.isActive()!=desired) command.toggleAction();
            return new JObject{["pawnId"]=id,["pawnName"]=pawn.LabelShortCap,["drafted"]=pawn.Drafted,["fireAtWill"]=pawn.drafter.FireAtWill}.ToString(Formatting.None);
        }
        private static int RequiredInt(JObject a,string key)
        {
            if(a[key]?.Type!=JTokenType.Integer) throw new ArgumentException("Supply integer "+key);
            return a.Value<int>(key);
        }
        private static FloatMenuOptionProvider Provider(string name)
        {
            switch(name) {
                case "DraftedMove": return new FloatMenuOptionProvider_DraftedMove();
                case "DraftedAttack": return new FloatMenuOptionProvider_DraftedAttack();
                case "RescuePawn": return new FloatMenuOptionProvider_RescuePawn();
                default: throw new ArgumentException("Choose a provider returned by pawns_orders.");
            }
        }
        private static T WithOptions<T>(Map map,JObject args,string providerName,Func<Pawn,List<FloatMenuOption>,T> use)
        {
            var pawn=Actor(map,RequiredInt(args,"pawnId"));
            Thing target=null; IntVec3 cell;
            if(args["targetId"]!=null) {
                if(args["x"]!=null || args["z"]!=null) throw new ArgumentException("Supply targetId or x,z, not both.");
                int targetId=RequiredInt(args,"targetId");
                target=map.listerThings.AllThings.FirstOrDefault(t=>t.thingIDNumber==targetId && t.Spawned && !t.Position.Fogged(map));
                if(target==null) throw new ArgumentException("Target is no longer visible on this map. Query again.");
                cell=target.Position;
            } else {
                if(args["x"]==null || args["z"]==null) throw new ArgumentException("Supply targetId from a query OR both x and z. Pawn ID alone identifies the actor, not the order target.");
                cell=new IntVec3(RequiredInt(args,"x"),0,RequiredInt(args,"z"));
                if(!cell.InBounds(map)) throw new ArgumentException("Cell is outside this map.");
            }
            if(providerName=="NativeMenu") {
                if(DebugSettings.godMode) throw new ArgumentException("Disable god mode before issuing manager orders.");
                var allowed=FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn(pawn);
                if(!allowed.Accepted) throw new ArgumentException(allowed.Reason??"Native pawn menu is unavailable.");
                var savedProvider=FloatMenuMakerMap.currentProvider;
                var savedPawn=FloatMenuMakerMap.makingFor;
                try {
                    FloatMenuMakerMap.currentProvider=null;
                    FloatMenuContext nativeContext;
                    var nativeOptions=FloatMenuMakerMap.GetOptions(new List<Pawn>{pawn},cell.ToVector3Shifted(),out nativeContext);
                    return use(pawn,nativeOptions.Where(o=>o!=null).ToList());
                } finally { FloatMenuMakerMap.currentProvider=savedProvider; FloatMenuMakerMap.makingFor=savedPawn; }
            }
            var previous=FloatMenuMakerMap.currentProvider;
            var provider=Provider(providerName);
            try {
                // Native menu contexts consult this scoped pointer for pawn eligibility.
                FloatMenuMakerMap.currentProvider=provider;
                var context=new FloatMenuContext(new List<Pawn>{pawn},cell.ToVector3Shifted(),map);
                if(context.FirstSelectedPawn==null || !provider.SelectedPawnValid(pawn,context) || !provider.Applies(context))
                    return use(pawn,new List<FloatMenuOption>());
                var options=new List<FloatMenuOption>();
                options.AddRange(provider.GetOptions(context));
                if(target!=null && provider.TargetThingValid(target,context)) {
                    options.AddRange(provider.GetOptionsFor(target,context));
                    if(target is Pawn targetPawn) options.AddRange(provider.GetOptionsFor(targetPawn,context));
                }
                return use(pawn,options.Where(o=>o!=null).ToList());
            } finally { FloatMenuMakerMap.currentProvider=previous; }
        }
        public static JObject Inspect(Map map,JObject args)
        {
            if(args["targetId"]==null && args["x"]==null && args["z"]==null) {
                Actor(map,RequiredInt(args,"pawnId"));
                return new JObject{["error"]="target_required",["orders"]=new JArray(),
                    ["next"]="Select a targetId from pendingWork, items_list or buildings_list, then call selection_inspect with targetId and pawnId. For an empty cell supply pawnId,x,z to pawns_orders. Current jobs are observations, not menu actions."};
            }
            var rows=new JArray();
            foreach(string provider in Providers) WithOptions(map,args,provider,(pawn,options)=> {
                foreach(var option in options) {
                    bool enabled=option.action!=null && !option.Disabled;
                    var row=new JObject{["label"]=option.Label,["enabled"]=enabled};
                    if(enabled) row["actionId"]=SaveHandle(map,args,provider,option.Label);
                    rows.Add(row);
                }
                return true;
            });
            return new JObject{["pawnId"]=RequiredInt(args,"pawnId"),["orders"]=rows,
                ["note"]="Native right-click choices for this pawn and target. Disabled labels include game reasons. No choices means this provider offers no applicable order. Prioritized work uses normal work eligibility; draft only for combat/movement. targetId selects its cell; the native menu can also include other things at that cell. Pass a returned actionId to pawns_order. Native confirmation/targeting dialogs still require player input."};
        }
        public static string Execute(Map map,JObject args)
        {
            if(args["actionId"]!=null) {
                CheckHandles();
                string id=args.Value<string>("actionId");
                if(id==null || !Handles.TryGetValue(id,out var saved) || saved.Value<int>("mapId")!=map.uniqueID)
                    throw new ArgumentException("Action handle expired or belongs to another map. Inspect the selection again.");
                args=(JObject)saved.DeepClone();
                Handles.Remove(id); // A native choice is a one-shot command, not a repeatable job recipe.
            }
            string provider=args.Value<string>("provider"),label=args.Value<string>("option");
            if(string.IsNullOrWhiteSpace(label)) throw new ArgumentException("Copy the native option label from pawns_orders.");
            return WithOptions(map,args,provider,(pawn,options)=> {
                var matching=options.Where(o=>o.Label==label).ToList();
                if(matching.Count!=1 || matching[0].Disabled || matching[0].action==null)
                    throw new ArgumentException("Native order is no longer available or is disabled. Query pawns_orders again; nothing changed.");
                matching[0].action();
                return new JObject{["pawnId"]=pawn.thingIDNumber,["pawnName"]=pawn.LabelShortCap,["selectedOrder"]=label,["drafted"]=pawn.Drafted,
                    ["currentJob"]=pawn.CurJob?.def.defName,["note"]="Native menu action invoked. The order may be queued or rejected by subsequent game conditions; this is not a claim of movement, damage or rescue completion. See pawn state and notifications."}.ToString(Formatting.None);
            });
        }
    }
}
