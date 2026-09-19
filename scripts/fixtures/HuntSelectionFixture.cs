using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    public sealed class HuntSelectionFixture
    {
        [Tool("test/hunt_selection_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Add a deer and three muffalo, an enabled rifle hunter and a butcher bill to the disposable empty-channel food fixture.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("Loaded map required.");
                if (!Find.TickManager.Paused) throw new InvalidOperationException("Paused map required.");
                var hunter = map.mapPawns.FreeColonistsSpawned.First(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Hunting)
                    && !p.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamed("Cooking")) && !p.Downed && p.equipment != null);
                foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
                {
                    pawn.jobs.StopAll();
                    pawn.drafter.Drafted = false;
                    foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                        if (!pawn.WorkTypeIsDisabled(work)) pawn.workSettings.SetPriority(work, 0);
                }
                hunter.workSettings.SetPriority(WorkTypeDefOf.Hunting, 1);
                hunter.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed("Cooking"), 4);
                for (var hour = 0; hour < 24; hour++) hunter.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
                hunter.skills.GetSkill(SkillDefOf.Shooting).Level = 20;
                if (hunter.equipment.Primary != null) hunter.equipment.DestroyAllEquipment();
                hunter.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(ThingDef.Named("Gun_BoltActionRifle")));
                var cells = GenRadial.RadialCellsAround(hunter.Position, 20, true)
                    .Where(c => c.InBounds(map) && !c.Fogged(map) && c.Standable(map)
                        && c.GetEdifice(map) == null && hunter.CanReach(c, PathEndMode.OnCell, Danger.None)
                        && c.DistanceToSquared(hunter.Position) >= 25).Take(8).ToList();
                if (cells.Count < 8) throw new InvalidOperationException("Not enough safe fixture cells.");
                var bench = (Building)ThingMaker.MakeThing(ThingDef.Named("ButcherSpot"));
                bench.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(bench, cells[0], map);
                var bill = (Bill_Production)DefDatabase<RecipeDef>.GetNamed("ButcherCorpseFlesh").MakeNewBill(null);
                bill.repeatMode = BillRepeatModeDefOf.Forever;
                ((IBillGiver)bench).BillStack.AddBill(bill);
                Pawn Spawn(string kind, int cell)
                {
                    var pawn = PawnGenerator.GeneratePawn(PawnKindDef.Named(kind), null);
                    GenSpawn.Spawn(pawn, cells[cell], map);
                    pawn.SetForbidden(false, false);
                    var reason = NativeHuntAcquisition.Ineligible(pawn);
                    if (reason != null) throw new InvalidOperationException(kind + ": " + reason);
                    return pawn;
                }
                var deer = Spawn("Deer", 1);
                var herd = Enumerable.Range(2, 3).Select(i => Spawn("Muffalo", i)).ToList();
                return new { success = true, deer = deer.GetUniqueLoadID(), herd = herd.Select(p => p.GetUniqueLoadID()).ToArray() };
            }, cancellationToken);
        }
    }
}
