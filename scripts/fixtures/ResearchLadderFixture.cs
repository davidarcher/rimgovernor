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
    // baseline without a player target. Prepare seeds only the first rung a
    // few points short of done so the second rung is selected within a
    // minute-scale watch; the simple research bench is the service's own to
    // build (#254). The starter shell it furnishes is the fixture's
    // (FixtureHut): a roofed wood hut with a sleeping spot per colonist so
    // the initial shelter is met and the bench rung is not held behind a
    // whole shell build. Audit reads the live research state back.
    public sealed class ResearchLadderFixture
    {
        [Tool("test/research_ladder_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable fixture: advance the named project (default Stonecutting) to 97% of its base cost (IsFinished compares real progress to baseCost; a tribal colony still owes the tech-level factor on the rest), build one roofed wood hut with a sleeping spot per colonist and wood and steel beside its door, and move every colonist inside. Spawns no research bench: the research ladder builds its own in the hut.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "ResearchProjectDef to advance (default Stonecutting).")] string project = "Stonecutting")
        {
            var name = string.IsNullOrEmpty(project) ? "Stonecutting" : project;
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var def = DefDatabase<ResearchProjectDef>.GetNamed(name);
                var manager = Find.ResearchManager;
                var progress = typeof(ResearchManager).GetField("progress", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(manager) as Dictionary<ResearchProjectDef, float>;
                if (progress == null) throw new InvalidOperationException("ResearchManager.progress unavailable.");
                progress[def] = def.baseCost * 0.97f;

                // One 9x9 ring (7x7 inside): the middle rows stay free for
                // the bench.
                var hut = FixtureHut.Build(map, 9);
                // The bench's materials: 75 stuff and 25 steel, which the
                // tribal baseline holds none of. Supply is another goal's
                // domain, so the fixture drops both beside the door.
                FixtureHut.DropOutside(map, hut, ThingDefOf.WoodLog, 4 * ThingDefOf.WoodLog.stackLimit);
                FixtureHut.DropOutside(map, hut, ThingDefOf.Steel, ThingDefOf.Steel.stackLimit);

                return new { success = true, project = name, progress = def.ProgressPercent, finished = def.IsFinished,
                    current = manager.GetProject()?.defName, researchBenches = map.listerBuildings.allBuildingsColonist.OfType<Building_ResearchBench>().Count(),
                    hut = hut.Summary(), tick = Find.TickManager.TicksGame };
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
