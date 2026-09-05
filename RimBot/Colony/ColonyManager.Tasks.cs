using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Verse;
using RimWorld;
namespace RimBot.Colony
{
    public sealed partial class ColonyManager
    {
        private readonly TaskLedger taskLedger=new TaskLedger();
        private string taskLedgerJson="";
        public JArray TrackedTasks=>taskLedger.View(mapId<0?(Find.CurrentMap?.uniqueID??-1):mapId);
        public JArray ActiveTrackedTasks=>new JArray(TrackedTasks.Where(TaskLedger.IsActive));
        public void DismissTrackedTask(string id)
        {
            int activeMap=mapId<0?(Find.CurrentMap?.uniqueID??-1):mapId;
            if(!taskLedger.Dismiss(activeMap,id)) return;
            nextObservation=0;
            Record("Removed work entry from tracking. Colony orders are unchanged.");
        }
        private void SaveTasks()
        {
            if(Scribe.mode==LoadSaveMode.Saving) taskLedgerJson=taskLedger.Serialize();
            Scribe_Values.Look(ref taskLedgerJson,"managerTaskLedger","");
            if(Scribe.mode==LoadSaveMode.PostLoadInit) {
                try { taskLedger.Load(taskLedgerJson); }
                catch(Exception ex) { Log.Warning("[RimBot] Could not restore tracked work: "+ex.Message); }
            }
        }
        private void ObserveTasks(Map map,int tick)
        {
            string before=taskLedger.DecisionKey(map.uniqueID);
            foreach(var row in taskLedger.Rows.OfType<JObject>().Where(r=>r.Value<int>("mapId")==map.uniqueID && r.Value<string>("tool")=="architect_build" && TaskLedger.IsActive(r))) {
                var args=row["args"]; var cell=new IntVec3(args.Value<int>("x"),0,args.Value<int>("z"));
                if(!cell.InBounds(map) || cell.Fogged(map)) continue;
                var thing=cell.GetThingList(map).FirstOrDefault(t=>t.Position==cell &&
                    (t.def.entityDefToBuild?.defName??t.def.defName)==args.Value<string>("defName") &&
                    (args["material"]==null || (t is IConstructible constructible?constructible.EntityToBuildStuff():t.Stuff)?.defName==args.Value<string>("material")));
                row["materials"]=thing==null?new JArray():ColonyObserver.Materials(thing);
                string stage=thing is Frame?"frame":thing is Blueprint?"blueprint":thing is Building?"built":"missing";
                taskLedger.ObserveConstruction(row,tick,stage,thing is Frame frame?frame.workDone:0);
            }
            if(before!=taskLedger.DecisionKey(map.uniqueID) && Automatic) executionPending=true;
        }
    }
}
