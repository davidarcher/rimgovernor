using UnityEngine;
using Verse;
using Newtonsoft.Json.Linq;
namespace RimBot.Colony
{
    public sealed class DailyPlanWindow : Window
    {
        private Vector2 scroll;
        private float contentHeight=300;
        public override Vector2 InitialSize=>new Vector2(560,420);
        public DailyPlanWindow()
        {
            doCloseX=true; draggable=true; preventCameraMotion=false; absorbInputAroundWindow=false;
        }
        public override void DoWindowContents(Rect rect)
        {
            var manager=ColonyManager.Current;
            if(manager==null) return;
            Widgets.Label(new Rect(0,0,rect.width-40,30),"Today's plan");
            var body=new Rect(0,38,rect.width,rect.height-38);
            Widgets.BeginScrollView(body,ref scroll,new Rect(0,0,body.width-20,contentHeight));
            var listing=new Listing_Standard(); listing.Begin(new Rect(0,0,body.width-20,10000));
            listing.Label(string.IsNullOrWhiteSpace(manager.DayBrief)?"No daily plan yet. It is prepared in Automate mode after the seasonal strategy.":manager.DayBrief);
            listing.GapLine(); listing.Label("Next order");
            listing.Label(string.IsNullOrWhiteSpace(manager.Plan)?"No orders planned.":manager.Plan);
            listing.GapLine(); listing.Label("Tracked work");
            foreach(var task in manager.TrackedTasks) {
                var args=task["args"];
                string title=args?["defName"]?.ToString()??task["tool"].ToString().Replace('_',' ');
                string project=task["projectId"]?.ToString();
                listing.Label(title+" - "+task["state"]+(!string.IsNullOrWhiteSpace(project)?" ("+project+")":""));
                if(task["materials"] is Newtonsoft.Json.Linq.JArray materials)
                    foreach(var material in materials)
                        if(material["needed"].Value<int>()>0)
                            listing.Label(material["label"]+": need "+material["needed"]+"; "+material["allowedOnMap"]+" allowed, "+material["forbiddenOnMap"]+" forbidden on map.");
                if(task["state"].ToString()=="Rejected") listing.Label(task["result"].ToString());
            }
            contentHeight=listing.CurHeight+10; listing.End(); Widgets.EndScrollView();
        }
    }
}
