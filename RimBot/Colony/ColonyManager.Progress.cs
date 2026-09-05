using System;
namespace RimBot.Colony
{
    public sealed partial class ColonyManager
    {
        private double progressStarted;
        public string ProgressPhase { get; private set; }="Ready";
        public string LastStep { get; private set; }="";
        public int ReviewReads { get; private set; }
        public int ReviewOrders { get; private set; }
        public int ReviewFailures { get; private set; }
        public int ReviewRequests { get; private set; }
        public int ElapsedSeconds=>(int)Math.Max(0,Clock.Elapsed.TotalSeconds-progressStarted);
        private void BeginProgress(string phase)
        {
            ProgressPhase=phase; progressStarted=Clock.Elapsed.TotalSeconds;
            ReviewReads=0; ReviewOrders=0; ReviewFailures=0; ReviewRequests=0; LastStep="";
        }
        private void StepProgress(string name,bool success)
        {
            if(!success) ReviewFailures++;
            else if(ColonyTools.IsAction(name)) ReviewOrders++;
            else ReviewReads++;
            LastStep=(success?"Finished: ":"Could not finish: ")+ProgressLabel(name);
        }
        private static string ProgressLabel(string name)
        {
            switch(name) {
                case "selection_inspect": return "selection inspection";
                case "pawns_orders": return "available pawn orders";
                case "pawns_order": return "pawn order";
                case "architect_buildables": return "building options";
                case "architect_build": return "construction order";
                case "construction_list": return "construction progress";
                case "items_list": return "supplies";
                case "map_inspect": return "site inspection";
                case "pawns_inspect": case "pawns_list": return "colonist check";
                case "orders_allow_all": case "orders_allow": return "allow supplies";
                case "bills_list": return "production check";
                case "bills_add": case "bills_configure": return "production bill";
                case "manager_save_plan": return "updated next steps";
                default: return name.Replace('_',' ');
            }
        }
        public string ProgressCounts=>ReviewReads+" checks  /  "+ReviewOrders+" orders"+
            (ReviewFailures>0?"  /  "+ReviewFailures+" failed":"")+"  /  "+ReviewRequests+" model calls";
    }
}
