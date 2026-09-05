using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using UnityEngine;
using Verse;
namespace RimBot.Colony
{
    public class ManagerWindow : MainTabWindow
    {
        private Vector2 activityScroll, planScroll;
        private float activityHeight=200, planHeight=400;
        private const string DirectionControl="RimBot.PlayerDirection";
        private string direction="", goalDraft;
        private int tab;
        private string lastHistoryLine;
        private bool follow=true, options, showCompleted;
        private readonly System.Collections.Generic.HashSet<string> expanded=new System.Collections.Generic.HashSet<string>();
        public override Vector2 InitialSize=>new Vector2(Math.Min(1000,UI.screenWidth-40),Math.Min(780,UI.screenHeight-70));
        public ManagerWindow() { preventCameraMotion=false; absorbInputAroundWindow=false; closeOnAccept=false; forceCatchAcceptAndCancelEventEvenIfUnfocused=true; }
        public override void OnAcceptKeyPressed()
        {
            // Verse dispatches Accept before drawing the text area. Keep it from closing the tab.
            HandleDirectionEnter();
        }
        private void HandleDirectionEnter()
        {
            var input=Event.current;
            if(input==null || input.type!=EventType.KeyDown || input.shift ||
                (input.keyCode!=KeyCode.Return && input.keyCode!=KeyCode.KeypadEnter) ||
                GUI.GetNameOfFocusedControl()!=DirectionControl) return;
            SubmitDirection();
            input.Use();
        }
        private void SubmitDirection()
        {
            var manager=ColonyManager.Current;
            if(manager==null || string.IsNullOrWhiteSpace(direction)) return;
            manager.Steer(direction.Trim()); direction=""; follow=true;
            GUI.FocusControl(DirectionControl);
        }
        public override void DoWindowContents(Rect rect)
        {
            var manager=ColonyManager.Current;
            if(manager==null) { Widgets.Label(rect,"Open a colony first."); return; }
            manager.RefreshObjectives();
            if(goalDraft==null) goalDraft=manager.Goal;
            var color=GUI.color; var font=Text.Font;
            Text.Font=GameFont.Medium;
            Widgets.Label(new Rect(rect.x,rect.y,rect.width-200,34),"Colony manager"); Text.Font=GameFont.Small;
            if(Widgets.ButtonText(new Rect(rect.xMax-170,rect.y,170,32),manager.Automatic?"Automate  /  Pause":"Manual  /  Automate"))
                manager.SetControl(manager.Automatic?ManagerControl.Manual:ManagerControl.Automate);
            var status=new Rect(rect.x,rect.y+40,rect.width,58);
            Widgets.DrawBoxSolid(status,new Color(0.12f,0.16f,0.18f));
            string heading=manager.Busy?manager.ProgressPhase+new string('.',1+(manager.ElapsedSeconds%3))+"  "+manager.ElapsedSeconds+"s":manager.Status;
            Widgets.Label(new Rect(status.x+10,status.y+5,status.width-20,24),ActivitySummary.Short(heading,130));
            GUI.color=new Color(0.7f,0.77f,0.8f);
            Widgets.Label(new Rect(status.x+10,status.y+30,status.width-20,23),manager.ProgressCounts);
            TooltipHandler.TipRegion(status,manager.Status+"\n"+manager.LastStep+"\nOrders counts are accepted commands, not completed jobs."); GUI.color=color;
            var footer=new Rect(rect.x,rect.yMax-91,rect.width,91);
            var body=new Rect(rect.x,rect.y+108,rect.width,Math.Max(90,footer.y-rect.y-120));
            float leftWidth=(body.width-18)*0.54f;
            var chat=new Rect(body.x,body.y,leftWidth,body.height);
            var plans=new Rect(chat.xMax+18,body.y,body.width-leftWidth-18,body.height);
            Widgets.Label(new Rect(chat.x,chat.y,chat.width-110,26),"Colony conversation");
            if(Widgets.ButtonText(new Rect(chat.xMax-110,chat.y,110,24),follow?"Following latest":"Follow latest")) follow=!follow;
            if(follow && manager.History.LastOrDefault()!=lastHistoryLine) activityScroll.y=100000;
            lastHistoryLine=manager.History.LastOrDefault();
            DrawConversation(new Rect(chat.x,chat.y+32,chat.width,chat.height-32),manager);
            string[] tabs={"Today","Strategy","Work"};
            float tabWidth=(plans.width-8)/3;
            for(int i=0;i<3;i++) {
                GUI.color=tab==i?new Color(0.7f,0.88f,1f):color;
                if(Widgets.ButtonText(new Rect(plans.x+i*(tabWidth+4),plans.y,tabWidth,28),tabs[i])) { tab=i; planScroll=Vector2.zero; }
            }
            GUI.color=color;
            var planBody=new Rect(plans.x,plans.y+36,plans.width,plans.height-36);
            Widgets.BeginScrollView(planBody,ref planScroll,new Rect(0,0,planBody.width-20,planHeight));
            var l=new Listing_Standard(); l.Begin(new Rect(0,0,planBody.width-20,100000));
            if(tab==0) DrawToday(l,manager);
            else if(tab==1) DrawStrategy(l,manager);
            else DrawWork(l,manager);
            planHeight=l.CurHeight+12; l.End(); Widgets.EndScrollView();
            Widgets.DrawLineHorizontal(footer.x,footer.y,footer.width);
            Widgets.Label(new Rect(footer.x,footer.y+7,footer.width-120,24),"Give direction  ·  Enter to send, Shift+Enter for a new line");
            HandleDirectionEnter();
            GUI.SetNextControlName(DirectionControl);
            direction=Widgets.TextArea(new Rect(footer.x,footer.y+34,footer.width-110,52),direction);
            if(direction.Length>600) direction=direction.Substring(0,600);
            bool previousEnabled=GUI.enabled; GUI.enabled=!string.IsNullOrWhiteSpace(direction);
            if(Widgets.ButtonText(new Rect(footer.xMax-100,footer.y+34,100,52),"Send")) SubmitDirection();
            GUI.enabled=previousEnabled; GUI.color=color; Text.Font=font;
        }
        private void DrawConversation(Rect rect,ColonyManager manager)
        {
            Widgets.BeginScrollView(rect,ref activityScroll,new Rect(0,0,rect.width-20,activityHeight));
            var l=new Listing_Standard(); l.Begin(new Rect(0,0,rect.width-20,100000));
            if(manager.History.Count==0) l.Label("Your directions and colony updates will appear here.");
            foreach(string line in manager.History) {
                string text=ActivitySummary.Short(line,420);
                float height=Text.CalcHeight(text,rect.width-36)+12;
                var row=l.GetRect(height);
                if(line.Contains(" You:")) Widgets.DrawBoxSolid(row,new Color(0.13f,0.2f,0.25f));
                Widgets.Label(row.ContractedBy(6),text); TooltipHandler.TipRegion(row,line); l.Gap(5);
            }
            if(manager.Busy) { l.GapLine(); l.Label(manager.ProgressPhase+"..."); if(!string.IsNullOrEmpty(manager.LastStep)) l.Label(manager.LastStep); }
            activityHeight=l.CurHeight+12; l.End(); Widgets.EndScrollView();
        }
        private void DrawToday(Listing_Standard l,ColonyManager manager)
        {
            foreach(var need in manager.Objectives) {
                var row=l.GetRect(25); string title=need.Id=="safety"?"Equipment":need.Id;
                Widgets.Label(row,char.ToUpper(title[0])+title.Substring(1)+"  -  "+need.StateLabel);
                TooltipHandler.TipRegion(row,need.Evidence+"\n"+need.NextStep);
            }
            l.GapLine(); l.Label("Today's priorities");
            l.Label(string.IsNullOrWhiteSpace(manager.DayBrief)?"A daily plan will appear after the first strategy review.":manager.DayBrief);
            if(!string.IsNullOrWhiteSpace(manager.Plan)) { l.GapLine(); l.Label("Next"); l.Label(manager.Plan); }
            l.GapLine();
            if(l.ButtonText(options?"Hide colony settings":"Colony settings")) options=!options;
            if(!options) return;
            l.Label("Long-term direction"); goalDraft=l.TextEntry(goalDraft);
            if(goalDraft.Length>600) goalDraft=goalDraft.Substring(0,600);
            if(l.ButtonText("Apply direction") && !string.IsNullOrWhiteSpace(goalDraft)) { manager.Goal=goalDraft; manager.Steer("Long-term direction: "+goalDraft); }
            var selected=Find.Selector.SingleSelectedThing;
            if(selected!=null && selected.Map==Find.CurrentMap && (selected is Pawn || selected is Building))
                if(l.ButtonText("Use selected location as colony base")) { selected.Map.GetComponent<ColonyLocation>().SetCenter(selected.Position); manager.RefreshObjectives(true); }
            if(Find.CurrentMap!=null && PlayerConstruction.BrokenSpots(Find.CurrentMap).Count>0 && l.ButtonText("Repair old sleeping-spot orders"))
                manager.Record(PlayerConstruction.ReplaceBrokenSpots(Find.CurrentMap));
        }
        private void DrawStrategy(Listing_Standard l,ColonyManager manager)
        {
            bool enabled=GUI.enabled; GUI.enabled=!manager.Busy;
            if(l.ButtonText("Replan strategy")) manager.RegenerateStrategy(); GUI.enabled=enabled;
            if(manager.Strategy==null) { l.Label(manager.StrategyStatus); return; }
            var strategy=manager.Strategy;
            l.Gap(); l.Label("This season"); l.Label(strategy.Season);
            foreach(var project in strategy.Projects.OrderBy(p=>p.Value<int>("priority"))) {
                l.GapLine(); string id=project.Value<string>("id"), state=manager.ProjectState(project);
                if(l.ButtonText((expanded.Contains(id)?"- ":"+ ")+project["title"])) { if(!expanded.Remove(id)) expanded.Add(id); }
                l.Label(state);
                if(!expanded.Contains(id)) continue;
                l.Label(project.Value<string>("purpose"));
                foreach(string step in project["steps"].Values<string>()) l.Label("- "+step);
                l.Label("Completion: "+string.Join("; ",project["completeWhen"].Select(c=>c["metric"].ToString().Replace('_',' ')+" "+c["op"]+" "+c["value"])));
            }
            l.GapLine(); l.Label("Next year"); l.Label(strategy.Year); l.Label("Longer term"); l.Label(strategy.ThreeYears);
        }
        private void DrawWork(Listing_Standard l,ColonyManager manager)
        {
            if(l.ButtonText(showCompleted?"Hide work history":"Include work history")) showCompleted=!showCompleted;
            var tasks=manager.TrackedTasks.Where(t=>t.Value<string>("state")!="Rejected" && (showCompleted || TaskLedger.IsActive(t))).Reverse().ToList();
            if(tasks.Count==0) l.Label("No tracked orders yet. Plans appear under Today and Strategy.");
            foreach(var task in tasks) {
                l.GapLine(); string def=task["args"]?.Value<string>("defName");
                string title=def==null?task.Value<string>("tool").Replace('_',' '):DefDatabase<ThingDef>.GetNamedSilentFail(def)?.label??def;
                var titleRow=l.GetRect(28);
                Widgets.Label(new Rect(titleRow.x,titleRow.y,Math.Max(0,titleRow.width-78),titleRow.height),title+"  -  "+task["state"]);
                var dismiss=new Rect(titleRow.xMax-74,titleRow.y,74,24);
                TooltipHandler.TipRegion(dismiss,"Remove this entry from tracking. Does not cancel orders or remove buildings.");
                if(Widgets.ButtonText(dismiss,"Dismiss")) manager.DismissTrackedTask(task.Value<string>("id"));
                if(task["args"]?["x"]!=null) l.Label("Location: "+task["args"]["x"]+", "+task["args"]["z"]);
                if(task["materials"] is JArray materials) foreach(var material in materials.Where(m=>m.Value<int>("needed")>0))
                    l.Label(material["label"]+": need "+material["needed"]+"; "+material["allowedOnMap"]+" allowed, "+material["forbiddenOnMap"]+" forbidden.");
                if(task.Value<string>("state")=="Issued") l.Label("Order issued; completion has not been verified.");
            }
        }
        internal static void DrawActivity(Rect rect,ColonyManager manager,ref Vector2 scroll,ref float height,int count)
        {
            Widgets.BeginScrollView(rect,ref scroll,new Rect(0,0,rect.width-20,height));
            var l=new Listing_Standard(); l.Begin(new Rect(0,0,rect.width-20,100000));
            foreach(var line in manager.History.AsEnumerable().Reverse().Take(count)) l.Label(line);
            height=l.CurHeight+8; l.End(); Widgets.EndScrollView();
        }
    }
    // Kept for compatibility with an already-open activity window. Main UI uses one integrated window.
    public class ManagerActivityWindow : Window
    {
        private Vector2 scroll; private float height;
        public override Vector2 InitialSize=>new Vector2(850,650);
        public ManagerActivityWindow() { doCloseX=true; draggable=true; preventCameraMotion=false; absorbInputAroundWindow=false; }
        public override void DoWindowContents(Rect rect) { var manager=ColonyManager.Current; if(manager!=null) ManagerWindow.DrawActivity(rect,manager,ref scroll,ref height,100); }
    }
}
