using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Verse;
using RimWorld;
namespace RimBot.Colony
{
    public class StrategyWindow : Window
    {
        private Vector2 scroll;
        private readonly System.Collections.Generic.HashSet<string> expanded=new System.Collections.Generic.HashSet<string>();
        private bool longTerm;
        private float height=1000;
        public override Vector2 InitialSize => new Vector2(850,700);
        public StrategyWindow() { doCloseX=true; draggable=true; preventCameraMotion=false; absorbInputAroundWindow=false; }
        private static string MetricLabel(string metric)
        {
            if(metric.StartsWith("building:")) return (DefDatabase<ThingDef>.GetNamedSilentFail(metric.Substring(9))?.label??metric.Substring(9))+" built";
            if(metric.StartsWith("research:")) return (DefDatabase<ResearchProjectDef>.GetNamedSilentFail(metric.Substring(9))?.label??metric.Substring(9))+" researched (1 = complete)";
            return metric.Replace('_',' ');
        }
        public override void DoWindowContents(Rect rect)
        {
            var manager=ColonyManager.Current;
            if(manager==null) return;
            Widgets.Label(new Rect(0,0,rect.width-250,32),"Colony strategy");
            if(Widgets.ButtonText(new Rect(rect.width-225,0,190,28),manager.Busy?"Planning…":"Regenerate strategy")) manager.RegenerateStrategy();
            var body=new Rect(0,40,rect.width,rect.height-40);
            Widgets.BeginScrollView(body,ref scroll,new Rect(0,0,body.width-20,height));
            var l=new Listing_Standard(); l.Begin(new Rect(0,0,body.width-20,100000));
            l.Label(manager.StrategyStatus);
            if(l.ButtonText("Today's plan")) Find.WindowStack.Add(new DailyPlanWindow());
            var plan=manager.Strategy;
            if(plan!=null) {
                l.GapLine(); l.Label("This season");
                var seasonRow=l.GetRect(48); Widgets.Label(seasonRow,ActivitySummary.Short(plan.Season,180)); TooltipHandler.TipRegion(seasonRow,plan.Season);
                if(l.ButtonText(longTerm?"Hide long-term direction":"Long-term direction")) longTerm=!longTerm;
                if(longTerm) { l.Label("Next year: "+plan.Year); l.Label("Three years: "+plan.ThreeYears); }
                foreach(var project in plan.Projects.OrderBy(p=>p["priority"].Value<int>())) {
                    l.GapLine();
                    string id=project["id"].Value<string>(), state=manager.ProjectState(project);
                    if(l.ButtonText((expanded.Contains(id)?"− ":"+ ")+project["title"]+" · "+(state.StartsWith("Blocked:")?"Blocked":state))) {
                        if(!expanded.Remove(id)) expanded.Add(id);
                    }
                    if(!expanded.Contains(id)) continue;
                    l.Label(state);
                    l.Label(project["purpose"].Value<string>());
                    foreach(string step in project["steps"].Values<string>()) l.Label("• "+step);
                    l.Label("Complete when: "+string.Join("; ",project["completeWhen"].Select(c=>MetricLabel(c["metric"].Value<string>())+" "+(c["op"].Value<string>()=="atLeast"?"at least":"at most")+" "+c["value"])));
                    if(project["dependsOn"].Any()) l.Label("Depends on: "+string.Join(", ",project["dependsOn"].Values<string>().Select(dependencyId=>plan.Projects.Single(p=>p["id"].Value<string>()==dependencyId)["title"].Value<string>())));
                }
            }
            height=l.CurHeight+10; l.End(); Widgets.EndScrollView();
        }
    }
}
