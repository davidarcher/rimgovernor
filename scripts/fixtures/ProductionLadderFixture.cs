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
    // Issue #4 M4: the multi-stage production ladder (research -> bench ->
    // ingredient storage -> bill) proved on a save whose workshop room already
    // stands. Prepare seeds what the ladder does not build itself: ingredients
    // on the ground, a research bench, and Smithing research a few points
    // short of done so the derived EnsureResearch target finishes within a
    // minute-scale watch. Audit reads the same native state back.
    public sealed class ProductionLadderFixture
    {
        const string Project = "Smithing";

        [Tool("test/production_ladder_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable fixture: drop steel and wood near the colonists, spawn a simple research bench, and advance Smithing research to 97% of its base cost (IsFinished compares real progress to baseCost; a tribal colony still owes the tech-level factor on the rest).")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
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
                int Drop(ThingDef def, int count)
                {
                    var placed = 0;
                    while (placed < count) {
                        var stack = Math.Min(def.stackLimit, count - placed);
                        var thing = ThingMaker.MakeThing(def); thing.stackCount = stack;
                        if (!GenPlace.TryPlaceThing(thing, pawn.Position, map, ThingPlaceMode.Near)) throw new InvalidOperationException("No drop site for " + def.defName);
                        placed += stack;
                    }
                    return placed;
                }
                var steel = Drop(ThingDefOf.Steel, 150);
                var wood = Drop(ThingDefOf.WoodLog, 150);
                var project = DefDatabase<ResearchProjectDef>.GetNamed(Project);
                var manager = Find.ResearchManager;
                var progress = typeof(ResearchManager).GetField("progress", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(manager) as Dictionary<ResearchProjectDef, float>;
                if (progress == null) throw new InvalidOperationException("ResearchManager.progress unavailable.");
                progress[project] = project.baseCost * 0.97f;
                return new { success = true, researchBench = bench.GetUniqueLoadID(), steel, wood, project = Project,
                    progress = project.ProgressPercent, finished = project.IsFinished, tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/production_ladder_audit", Description = "Private read-only fixture: Smithing state, smithies with their room role and bills, stockpile zones with their room role and steel allowance, and the live gladius count.")]
        public async Task<object> Audit(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) throw new InvalidOperationException("No current map.");
                var project = DefDatabase<ResearchProjectDef>.GetNamed(Project);
                string RoleAt(IntVec3 cell) => cell.GetRoom(map)?.Role?.defName;
                var smithies = map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>()
                    .Where(b => b.def.defName == "FueledSmithy" || b.def.defName == "ElectricSmithy")
                    .Select(b => new { thingId = b.GetUniqueLoadID(), defName = b.def.defName, x = b.Position.x, z = b.Position.z, roomRole = RoleAt(b.Position),
                        powered = b.TryGetComp<CompPowerTrader>()?.PowerOn, fuel = b.TryGetComp<CompRefuelable>()?.Fuel,
                        bills = b.BillStack.Bills.Select(bill => bill.recipe.defName).ToArray() }).ToArray();
                var stockpiles = map.zoneManager.AllZones.OfType<Zone_Stockpile>().Select(z => new { id = z.ID, label = z.label, cells = z.Cells.Count,
                    roomRole = z.Cells.Count > 0 ? RoleAt(z.Cells[0]) : null, priority = z.settings.Priority.ToString(),
                    allowsSteel = z.settings.filter.Allows(ThingDefOf.Steel), allowedCount = z.settings.filter.AllowedDefCount,
                    steelStored = z.AllContainedThings.Where(t => t.def == ThingDefOf.Steel).Sum(t => t.stackCount) }).ToArray();
                var gladius = ThingDef.Named("MeleeWeapon_Gladius");
                var ground = map.listerThings.ThingsOfDef(gladius).Count(t => t.Spawned);
                var carried = map.mapPawns.FreeColonistsSpawned.Count(p => p.equipment?.Primary?.def == gladius);
                var researchers = map.mapPawns.FreeColonistsSpawned.Where(p => p.workSettings != null && p.workSettings.WorkIsActive(WorkTypeDefOf.Research)).Select(p => p.LabelShort).ToArray();
                return new { success = true, project = Project, finished = project.IsFinished, progress = project.ProgressPercent,
                    current = Find.ResearchManager.GetProject()?.defName, researchers,
                    researchBenches = map.listerBuildings.allBuildingsColonist.OfType<Building_ResearchBench>().Count(),
                    smithies, stockpiles, gladiusGround = ground, gladiusCarried = carried, tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
