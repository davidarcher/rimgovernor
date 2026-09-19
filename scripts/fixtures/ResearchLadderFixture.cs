using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Issue #230: the default research ladder walked on the Core tribal
    // baseline without a player target. Prepare seeds what the ladder does
    // not build itself: a simple research bench, and the first rung a few
    // points short of done so the second rung is selected within a
    // minute-scale watch. Audit reads the live research state back.
    public sealed class ResearchLadderFixture
    {
        [Tool("test/research_ladder_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable fixture: spawn a simple research bench near the colonists and advance the named project (default Stonecutting) to 97% of its base cost (IsFinished compares real progress to baseCost; a tribal colony still owes the tech-level factor on the rest).")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "ResearchProjectDef to advance (default Stonecutting).")] string project = "Stonecutting")
        {
            var name = string.IsNullOrEmpty(project) ? "Stonecutting" : project;
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var pawn = map.mapPawns.FreeColonistsSpawned.First();
                var benchDef = ThingDef.Named("SimpleResearchBench");
                var benchCell = GenRadial.RadialCellsAround(pawn.Position, 20, true).FirstOrDefault(c => c.InBounds(map) && !c.Fogged(map)
                    && GenConstruct.CanPlaceBlueprintAt(benchDef, c, Rot4.North, map).Accepted
                    && GenAdj.OccupiedRect(c, Rot4.North, benchDef.size).Cells.All(cell => cell.Standable(map) && cell.GetFirstBuilding(map) == null));
                if (!benchCell.IsValid) throw new InvalidOperationException("No research bench site.");
                var bench = ThingMaker.MakeThing(benchDef, GenStuff.DefaultStuffFor(benchDef));
                bench.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(bench, benchCell, map, Rot4.North);
                var def = DefDatabase<ResearchProjectDef>.GetNamed(name);
                var manager = Find.ResearchManager;
                var progress = typeof(ResearchManager).GetField("progress", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(manager) as Dictionary<ResearchProjectDef, float>;
                if (progress == null) throw new InvalidOperationException("ResearchManager.progress unavailable.");
                progress[def] = def.baseCost * 0.97f;
                return new { success = true, researchBench = bench.GetUniqueLoadID(), project = name, progress = def.ProgressPercent, finished = def.IsFinished,
                    current = manager.GetProject()?.defName, tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/research_ladder_audit", Description = "Private read-only fixture: the current research project, every finished project, the named project's progress, research benches and researchers.")]
        public async Task<object> Audit(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "ResearchProjectDef to report progress for (default Stonecutting).")] string project = "Stonecutting")
        {
            var name = string.IsNullOrEmpty(project) ? "Stonecutting" : project;
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) throw new InvalidOperationException("No current map.");
                var def = DefDatabase<ResearchProjectDef>.GetNamed(name);
                var finished = DefDatabase<ResearchProjectDef>.AllDefsListForReading.Where(p => p.IsFinished).Select(p => p.defName).OrderBy(p => p).ToArray();
                var researchers = map.mapPawns.FreeColonistsSpawned.Where(p => p.workSettings != null && p.workSettings.WorkIsActive(WorkTypeDefOf.Research)).Select(p => p.LabelShort).ToArray();
                var current = Find.ResearchManager.GetProject();
                return new { success = true, project = name, finished = def.IsFinished, progress = def.ProgressPercent,
                    current = current?.defName, currentProgress = current?.ProgressPercent, finishedProjects = finished, researchers,
                    researchBenches = map.listerBuildings.allBuildingsColonist.OfType<Building_ResearchBench>().Count(), tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
