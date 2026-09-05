using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
namespace RimBot.Colony
{
    public sealed partial class ColonyManager
    {
        private readonly DecisionMemory decisions=new DecisionMemory();
        private string decisionJson="";
        public int TotalToolCalls { get; private set; }
        public int RepeatedToolResults { get; private set; }
        public int RecoveryRequests { get; private set; }
        private void SaveDecisions()
        {
            if(Scribe.mode==LoadSaveMode.Saving) decisionJson=decisions.Serialize();
            Scribe_Values.Look(ref decisionJson,"managerDecisionMemory","");
            if(Scribe.mode==LoadSaveMode.PostLoadInit) {
                try { decisions.Load(decisionJson); } catch(Exception) { decisions.Clear(); }
            }
        }
        private JObject WorkFocus(Map map)
        {
            var pending=ColonyObserver.Orders(map);
            // Prefer unstaffed orders; expose evidence without prescribing a construction recipe.
            var focus=pending.OrderBy(p=>p["pawnsTargeting"].Count()).FirstOrDefault();
            if(focus==null) return new JObject{["pendingConstruction"]=0};
            var details=SelectionInspection.Read(map,new JObject{["targetId"]=focus["id"]});
            details.Remove("surroundings");
            details["pawnsTargeting"]=focus["pawnsTargeting"].DeepClone();
            details["pendingConstructionInPage"]=pending.Count;
            details["materialShortfalls"]=new JArray(pending.SelectMany(p=>p["materials"]).GroupBy(m=>m.Value<string>("defName")).Select(g=>new JObject{
                ["defName"]=g.Key,["neededAcrossPendingOrders"]=g.Sum(m=>m.Value<int>("needed")),
                ["shortfall"]=Math.Max(0,g.Sum(m=>m.Value<int>("needed"))-g.First().Value<int>("allowedOnMap")),
                ["allowedOnMap"]=g.First()["allowedOnMap"],["forbiddenOnMap"]=g.First()["forbiddenOnMap"]}));
            details["forbiddenSupplyIds"]=new JArray(details["nearbySupplies"].Where(s=>s.Value<bool>("forbidden")).Select(s=>s["id"]));
            details["alreadyAllowedSupplyIds"]=new JArray(details["nearbySupplies"].Where(s=>!s.Value<bool>("forbidden")).Select(s=>s["id"]));
            details["instruction"]="Only forbiddenSupplyIds can be helped by orders_allow; alreadyAllowedSupplyIds need no Allow call. Resolve or explain unfinished work before adding equivalent orders. Allowed supply counts are shared, not reserved per order; reachability and pawn eligibility still apply. Inspect the target with a pawnId for native work blockers. Wait when work is progressing; do not interrupt rest just to eliminate idle time.";
            return details;
        }
    }
}
