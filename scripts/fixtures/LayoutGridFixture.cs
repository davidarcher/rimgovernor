using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Issue #607: the layout/grid case proves the tiered colony layout on
    // the tribal baseline. Prepare finishes Stonecutting so the build tier
    // reads Masonry, stages the starter hut (FixtureHut) whose south-west
    // corner the controller fixes the colony grid on, and drops wood beside
    // its door; the field the controller then plans is the case's own.
    // Audit reads every finished player wall ring and growing zone back
    // with their cells so the case checks both footprints against the grid.
    public sealed class LayoutGridFixture
    {
        [Tool("test/layout_grid_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable fixture: finish the named research (default Stonecutting) with its prerequisites, build one roofed wood hut with a sleeping spot per colonist, drop wood beside its door and move every colonist inside. Plans no field: the field planner sites its own.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "ResearchProjectDef to finish (default Stonecutting).")] string project = "Stonecutting")
        {
            var name = string.IsNullOrEmpty(project) ? "Stonecutting" : project;
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var def = DefDatabase<ResearchProjectDef>.GetNamed(name);
                Finish(def);
                var hut = FixtureHut.Build(map, 9);
                FixtureHut.DropOutside(map, hut, ThingDefOf.WoodLog, 4 * ThingDefOf.WoodLog.stackLimit);
                return new { success = true, project = name, finished = def.IsFinished, hut = hut.Summary(),
                    hutOrigin = new { x = hut.Origin.x, z = hut.Origin.z }, hutSize = 9, tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/layout_grid_audit", Description = "Private read-only fixture: every finished player wall or door cell and every growing zone with its cells and crop.")]
        public async Task<object> Audit(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) return new { success = false, reason = "A disposable colony map is required." };
                var walls = map.listerBuildings.allBuildingsColonist
                    .Where(b => b.def.defName == "Wall" || b.def.defName == "Door")
                    .Select(b => new { x = b.Position.x, z = b.Position.z, def = b.def.defName }).ToList();
                var zones = map.zoneManager.AllZones.OfType<Zone_Growing>()
                    .Select(z => new { id = z.ID, label = z.label, crop = z.GetPlantDefToGrow()?.defName,
                        cells = z.Cells.Select(c => new { x = c.x, z = c.z }).ToList() }).ToList();
                return new { success = true, tick = Find.TickManager.TicksGame, walls, zones };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static void Finish(ResearchProjectDef project)
        {
            if (project.IsFinished) return;
            if (project.prerequisites != null) foreach (var p in project.prerequisites) Finish(p);
            Find.ResearchManager.FinishProject(project, false);
        }
    }
}
