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

        private Vector2 overviewScroll, activityScroll;

        private float overviewHeight=400, activityHeight=200;

        public override Vector2 InitialSize => new Vector2(850f,720f);

        public override void DoWindowContents(Rect rect)

        {

            var manager=ColonyManager.Current;

            if(manager==null) { Widgets.Label(rect,"Open a colony first."); return; }

            manager.RefreshObjectives();

            if(Widgets.ButtonText(new Rect(rect.x,rect.y,180,32),"AI: "+manager.Control))

                manager.SetControl(manager.Automatic?ManagerControl.Manual:ManagerControl.Automate);

            Widgets.Label(new Rect(rect.x+195,rect.y,rect.width-195,48),manager.Status);

            float logHeight=Math.Min(200,rect.height*0.32f);

            var log=new Rect(rect.x,rect.yMax-logHeight,rect.width,logHeight);

            var overview=new Rect(rect.x,rect.y+54,rect.width,Math.Max(60,log.y-rect.y-64));

            Widgets.BeginScrollView(overview,ref overviewScroll,new Rect(0,0,overview.width-20,overviewHeight));

            var l=new Listing_Standard(); l.Begin(new Rect(0,0,overview.width-20,10000));

            l.Label(manager.Automatic?"Managing the colony when conditions change. Pauses with the game.":"You control the colony. Select Automate to enable AI.");

            foreach(var item in manager.Objectives) {

                var row=l.GetRect(25);

                var previous=GUI.color;

                GUI.color=item.State==ObjectiveState.Satisfied?new Color(0.65f,0.85f,0.65f):item.State==ObjectiveState.Blocked?new Color(1f,0.65f,0.5f):new Color(0.95f,0.85f,0.55f);

                string title=item.Id=="health"?"Health":item.Id=="safety"?"Safety":item.Id=="food"?"Food":item.Id=="shelter"?"Shelter":"Construction";

                Widgets.Label(row,title+"  ·  "+item.StateLabel);

                GUI.color=previous;

                TooltipHandler.TipRegion(row,item.Evidence+"\n\n"+item.NextStep);

            }

            var selected=Find.Selector.SingleSelectedThing;
            if(selected!=null && selected.Map==Find.CurrentMap && (selected is Pawn || selected is Building))
                if(l.ButtonText("Use selected " + selected.LabelShort + " as colony base")) {
                    selected.Map.GetComponent<ColonyLocation>().SetCenter(selected.Position);
                    manager.RefreshObjectives(true);
                    manager.Record("Colony base moved to " + selected.Position);
                }
            l.GapLine();

            l.Label("Direction (optional)");

            manager.Goal=l.TextEntry(manager.Goal);

            if(manager.Goal.Length>600) manager.Goal=manager.Goal.Substring(0,600);

            l.Gap(8);

            var strategyRow=l.GetRect(30);

            Widgets.Label(new Rect(strategyRow.x,strategyRow.y,strategyRow.width-170,30),"Seasonal strategy");

            if(Widgets.ButtonText(new Rect(strategyRow.xMax-160,strategyRow.y,160,26),"View strategy")) Find.WindowStack.Add(new StrategyWindow());

            if(manager.Strategy==null) l.Label(manager.StrategyStatus);

            else {

                string direction=manager.Strategy.Season;

                var directionRow=l.GetRect(26);

                Widgets.Label(directionRow,direction.Length>100?direction.Substring(0,100)+"…":direction);

                TooltipHandler.TipRegion(directionRow,direction);

                foreach(var project in manager.Strategy.Projects.OrderBy(p=>p["priority"].Value<int>()).Take(3)) {

                    var projectRow=l.GetRect(26);

                    string state=manager.ProjectState(project);

                    Widgets.Label(projectRow,project["title"]+" · "+(state.StartsWith("Blocked:")?"Blocked":state));

                    TooltipHandler.TipRegion(projectRow,state+"\n"+project["purpose"]);

                }

            }

            l.Gap(8);

            l.Label("Daily execution");

            l.Label(string.IsNullOrWhiteSpace(manager.Plan)?"No plan yet.":manager.Plan);

            overviewHeight=l.CurHeight+10; l.End(); Widgets.EndScrollView();

            Widgets.Label(new Rect(log.x,log.y,log.width-180,28),"Recent activity");

            if(Widgets.ButtonText(new Rect(log.xMax-160,log.y,160,26),"Expand activity")) Find.WindowStack.Add(new ManagerActivityWindow());

            DrawActivity(new Rect(log.x,log.y+32,log.width,log.height-32),manager,ref activityScroll,ref activityHeight,6);

        }

        internal static void DrawActivity(Rect rect,ColonyManager manager,ref Vector2 scroll,ref float height,int count)

        {

            Widgets.BeginScrollView(rect,ref scroll,new Rect(0,0,rect.width-20,height));

            var l=new Listing_Standard(); l.Begin(new Rect(0,0,rect.width-20,100000));

            if(manager.History.Count==0) l.Label("No AI activity yet.");

            foreach(var line in manager.History.AsEnumerable().Reverse().Take(count)) { l.Label(line); l.Gap(6); }

            height=l.CurHeight+8; l.End(); Widgets.EndScrollView();

        }

    }

    public class ManagerActivityWindow : Window

    {

        private Vector2 scroll;

        private float height=600;

        public override Vector2 InitialSize => new Vector2(850,650);

        public ManagerActivityWindow() { doCloseX=true; draggable=true; }

        public override void DoWindowContents(Rect rect)

        {

            var manager=ColonyManager.Current;

            if(manager==null) return;

            Widgets.Label(new Rect(0,0,rect.width-30,32),"AI activity · "+manager.Requests+" requests this hour · "+manager.Tokens+" tokens this session");

            ManagerWindow.DrawActivity(new Rect(0,40,rect.width,rect.height-40),manager,ref scroll,ref height,100);

        }

    }

}
