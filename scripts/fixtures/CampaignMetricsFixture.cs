using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Explicitly gated disposable setup. Never available in production builds.
    public sealed class CampaignMetricsFixture
    {
        private static Map fixtureMap;
        private static CellRect bounds;
        private static readonly List<Building_Bed> beds = new List<Building_Bed>();
        private static Zone_Stockpile storage;
        private static Thing rice;

        [Tool("test/campaign_metrics_fixture", Description = "Prepare disposable campaign metric cases; test builds only.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "sleep prepares the fixture; work enables ordinary hauling/construction.")] string action = "sleep")
        {
            return await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var pawns = map.mapPawns.FreeColonistsSpawned.ToList();
                if (action == "sleep")
                {
                    if (beds.Count != 0 && fixtureMap == map) throw new InvalidOperationException("Use a fresh disposable load");
                    fixtureMap = map; beds.Clear();
                    var origin = GenRadial.RadialCellsAround(pawns[0].Position, 45, true).First(c =>
                        new CellRect(c.x, c.z, 14, 12).All(p => p.InBounds(map) && !p.Fogged(map)
                            && p.GetTerrain(map).passability != Traversability.Impassable && map.zoneManager.ZoneAt(p) == null));
                    // Fixture preparation clears a disclosed disposable patch; all
                    // later sleeping, hauling, building and removal use normal jobs.
                    foreach(var c in new CellRect(origin.x,origin.z,14,12))
                        foreach(var thing in c.GetThingList(map).ToList())
                            if(!(thing is Pawn)) thing.Destroy(DestroyMode.Vanish);
                    bounds = new CellRect(origin.x, origin.z, 12, 7);
                    foreach (var c in bounds)
                    {
                        if (c.x == bounds.minX || c.x == bounds.maxX || c.z == bounds.minZ || c.z == bounds.maxZ)
                        {
                            var def = c.x == bounds.minX+6 && c.z == bounds.minZ ? "Door" : "Wall";
                            Spawn(def, c, map);
                        }
                        map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                    }
                    for (var i=0; i<10; i++)
                    {
                        var c = new IntVec3(bounds.minX+2+(i%8), 0, bounds.minZ+2+(i/8)*2);
                        beds.Add((Building_Bed)Spawn("SleepingSpot", c, map));
                    }
                    storage = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                    storage.label = "Metric storage";
                    map.zoneManager.RegisterZone(storage);
                    foreach (var c in new CellRect(bounds.minX+1,bounds.maxZ+2,3,3)) storage.AddCell(c);
                    storage.settings.filter.SetDisallowAll();
                    storage.settings.filter.SetAllow(DefDatabase<ThingDef>.GetNamed("RawRice"),true);
                    rice = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("RawRice"));
                    rice.stackCount=75;
                    GenSpawn.Spawn(rice,new IntVec3(bounds.maxX,0,bounds.maxZ+2),map);
                    rice.SetForbidden(true,false);
                    var wood=ThingMaker.MakeThing(ThingDefOf.WoodLog);wood.stackCount=75;
                    GenSpawn.Spawn(wood,new IntVec3(bounds.maxX,0,bounds.maxZ+3),map);wood.SetForbidden(false,false);
                    for (var i=0;i<pawns.Count;i++)
                    {
                        var pawn=pawns[i];
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                        pawn.pather.StopDead();
                        pawn.Position=new IntVec3(bounds.minX+1+i,0,bounds.minZ-1);
                        pawn.needs.rest.CurLevel=0.05f; pawn.needs.food.CurLevel=0.95f;
                        for(var hour=0;hour<24;hour++) pawn.timetable.SetAssignment(hour,TimeAssignmentDefOf.Sleep);
                        pawn.ownership.ClaimBedIfNonMedical(beds[i]);
                    }
                }
                else if(action == "work")
                {
                    if(fixtureMap != map || rice == null) throw new InvalidOperationException("Prepare sleep first");
                    rice.SetForbidden(false,false);
                    foreach(var pawn in pawns)
                    {
                        pawn.needs.rest.CurLevel=0.9f; pawn.needs.food.CurLevel=0.95f;
                        for(var hour=0;hour<24;hour++) pawn.timetable.SetAssignment(hour,TimeAssignmentDefOf.Work);
                        foreach(var work in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                            if(!pawn.WorkTypeIsDisabled(work)) pawn.workSettings.SetPriority(work,
                                work.defName == "Hauling" || work.defName == "Construction" ? 1 : 0);
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    }
                }
                else if(action == "rest")
                {
                    if(fixtureMap != map) throw new InvalidOperationException("Prepare sleep first");
                    foreach(var pawn in pawns)
                    {
                        pawn.needs.rest.CurLevel=0.05f; pawn.needs.food.CurLevel=0.95f;
                        for(var hour=0;hour<24;hour++) pawn.timetable.SetAssignment(hour,TimeAssignmentDefOf.Sleep);
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    }
                }
                else throw new ArgumentException("Unknown fixture action");
                return (object)new { success=true, action, anchor=BridgeCommon.Pos(bounds.CenterCell),
                    beds=beds.Select(b=>new {thingId=b.GetUniqueLoadID(),position=BridgeCommon.Pos(b.Position)}).ToArray(),
                    zoneId=storage.ID,riceId=rice.GetUniqueLoadID(),
                    constructionCell=BridgeCommon.Pos(new IntVec3(bounds.maxX+1,0,bounds.maxZ+4)) };
            },cancellationToken);
        }

        private static Thing Spawn(string name, IntVec3 cell, Map map)
        {
            var def=DefDatabase<ThingDef>.GetNamed(name);
            var thing=ThingMaker.MakeThing(def,def.MadeFromStuff ? ThingDefOf.WoodLog : null);
            thing.SetFaction(Faction.OfPlayer);
            return GenSpawn.Spawn(thing,cell,map,Rot4.North);
        }
    }
}
