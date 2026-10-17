#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Brain.Domain;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Fx = RimGovernor.Fixtures.Contracts;

namespace HomeBridge.BridgeTools;

/// <summary>Prepared physical preconditions only. Ordinary review chooses whether and who finishes either frame.</summary>
public sealed class ServiceInvestmentFixture
{
    private static Frame? heater, spare;
    private static FixtureHut.Result? hut;
    private static bool patched, active, worked;

    [Tool("test/service_shelter_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Prepare a cold occupied room, partly funded heater and unrelated lower-tier wall competing for five steel. No chosen task or assignment.")]
    public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            active = false;
            var map = Find.CurrentMap;
            if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused lab required.");
            hut = FixtureHut.Build(map, 5, map.Center - new IntVec3(2, 0, 2), null, 2);
            hut.Room.Temperature = -10;
            foreach (var pawn in hut.People)
            {
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                pawn.needs.food.CurLevelPercentage = 1;
                pawn.needs.rest.CurLevelPercentage = pawn == hut.People[0] ? .2f : .95f;
                pawn.needs.joy.CurLevelPercentage = 1;
                for (var hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);
                foreach (var type in DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(t => !pawn.WorkTypeIsDisabled(t))) pawn.workSettings.SetPriority(type, 0);
                pawn.skills.GetSkill(SkillDefOf.Construction).Level = 12;
            }
            Frame Fund(ThingDef definition, IntVec3 cell, ThingDef? stuff = null, int missingSteel = 0)
            {
                var frame = (Frame)ThingMaker.MakeThing(definition.frameDef, stuff);
                frame.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(frame, cell, map);
                foreach (var cost in frame.TotalMaterialCost())
                { var item = ThingMaker.MakeThing(cost.thingDef); item.stackCount = cost.count - (cost.thingDef == ThingDefOf.Steel ? missingSteel : 0); if (item.stackCount > 0) frame.resourceContainer.TryAdd(item, true); else item.Destroy(); }
                frame.workDone = Math.Max(0, frame.WorkToBuild - 100);
                map.areaManager.Home[cell] = true;
                return frame;
            }
            var heaterCell = hut.Interior.Last();
            heater = Fund(ThingDef.Named("Heater"), heaterCell, missingSteel: 5);
            spare = Fund(ThingDefOf.Wall, hut.Origin - new IntVec3(3, 0, 0), ThingDefOf.Steel, missingSteel: 5);
            ConstructionSkillGuard.SetTier(heater, 5); ConstructionSkillGuard.SetTier(spare, 0);
            var steel = ThingMaker.MakeThing(ThingDefOf.Steel); steel.stackCount = 5;
            GenSpawn.Spawn(steel, hut.Interior.First(), map);
            var generator = ThingMaker.MakeThing(ThingDef.Named("WoodFiredGenerator"));
            generator.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(generator, new IntVec3(heaterCell.x, 0, hut.Origin.z + 7), map);
            var fuel = generator.TryGetComp<CompRefuelable>(); fuel.Refuel(fuel.Props.fuelCapacity);
            foreach (var cell in Enumerable.Range(heaterCell.z, 6).Select(z => new IntVec3(heaterCell.x, 0, z)))
            {
                var conduit = ThingMaker.MakeThing(ThingDefOf.PowerConduit); conduit.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(conduit, cell, map);
            }
            map.powerNetManager.UpdatePowerNetsAndConnections_First();
            worked = false;
            if (!patched)
            {
                new Harmony("rimgovernor.fixture.service-investment").Patch(AccessTools.Method(typeof(Pawn_JobTracker), "JobTrackerTick"),
                    postfix: new HarmonyMethod(typeof(ServiceInvestmentFixture), nameof(ObserveWork)));
                patched = true;
            }
            active = true;
            return FixtureKit.Reply(new Fx.ServiceShelterPrepared { Success = true, X = hut.Origin.x + 1, Z = hut.Origin.z + 1, DoorX = hut.Door.x, DoorZ = hut.Door.z });
        }, ct).ConfigureAwait(false);

    private static void ObserveWork(Pawn ___pawn)
    {
        if (!active || heater == null || ___pawn.CurJob?.targetA.Thing != heater) return;
        var execution = NativePawnActivities.Read(___pawn);
        if (execution?.Method.StartsWith("coupled-", StringComparison.Ordinal) != true) return;
        if (___pawn.CurJobDef?.defName == "FinishFrame") worked = true;
    }

    [Tool("test/service_shelter_read", Description = "UNSAFE FOR MODEL EXECUTION. Read actual normal-Method work, completed heater, room temperature and deferred spare frame.")]
    public async Task<object> Read(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            if (hut == null || heater == null || spare == null) throw new InvalidOperationException("Prepare shelter first.");
            var map = Find.CurrentMap;
            var complete = hut.Interior.Select(c => c.GetEdifice(map)).OfType<Building_Heater>().SingleOrDefault();
            return FixtureKit.Reply(new Fx.ServiceShelterRead
            {
                Tick = Find.TickManager.TicksGame, Worked = worked, Completed = complete != null,
                Powered = complete?.TryGetComp<CompPowerTrader>()?.PowerOn == true,
                Temperature = hut.Interior[0].GetTemperature(map), SparePresent = spare.Spawned,
                SpareRemainingWork = spare.Spawned ? spare.WorkToBuild - spare.workDone : 0,
            });
        }, ct).ConfigureAwait(false);
}
