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
    public sealed class SubdueFixture
    {
        [Tool("test/subdue_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Stage disposable colonists for native subdue acceptance.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var hut = FixtureHut.Build(map, 7);
                var pawn = hut.People.First(p => !p.WorkTagIsDisabled(WorkTags.Violent));
                var target = hut.People.First(p => p != pawn);
                pawn.skills.GetSkill(SkillDefOf.Melee).Level = 20;
                target.skills.GetSkill(SkillDefOf.Melee).Level = 0;
                target.equipment.DestroyAllEquipment();
                var bed = hut.Interior.SelectMany(c => c.GetThingList(map)).OfType<Building_Bed>().First();
                bed.ForPrisoners = false;
                bed.CompAssignableToPawn.TryAssignPawn(target);
                return new { success = true, pawn = pawn.GetUniqueLoadID(), target = target.GetUniqueLoadID(), bed = bed.GetUniqueLoadID() };
            }, cancellationToken);
        }
        [Tool("test/subdue_stage", Description = "UNSAFE FOR MODEL EXECUTION. Stage normal, ranged or unarmed Berserk subdue preconditions.")]
        public async Task<object> Stage(IRimBridgeContext ctx, CancellationToken cancellationToken, string pawnId, string targetId, string scenario)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var pawn = map.mapPawns.AllPawnsSpawned.Single(p => p.GetUniqueLoadID() == pawnId);
                var target = map.mapPawns.AllPawnsSpawned.Single(p => p.GetUniqueLoadID() == targetId);
                if (target.InMentalState) target.MentalState.RecoverFromState();
                pawn.drafter.Drafted = false;
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                pawn.equipment.DestroyAllEquipment();
                if (scenario == "ranged") pawn.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(ThingDefOf.Gun_Autopistol));
                target.drafter.Drafted = false;
                target.jobs.EndCurrentJob(JobCondition.InterruptForced);
                if (scenario != "normal" && !target.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Berserk, forced: true, forceWake: true))
                    throw new InvalidOperationException("Berserk did not start.");
                return new { success = true };
            }, cancellationToken);
        }
        [Tool("test/subdue_inspect", Description = "Read-only living containment postcondition for the subdue fixture.")]
        public async Task<object> Inspect(IRimBridgeContext ctx, CancellationToken cancellationToken, string targetId, string pawnId)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var target = Find.CurrentMap.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == targetId);
                var pawn = Find.CurrentMap.mapPawns.AllPawnsSpawned.Single(p => p.GetUniqueLoadID() == pawnId);
                return new { alive = target != null && !target.Dead, downed = target?.Downed, aggro = target?.InAggroMentalState,
                    blunt = pawn.CurJob?.verbToUse?.verbProps.meleeDamageDef == DamageDefOf.Blunt,
                    prisoner = target?.IsPrisonerOfColony, bed = target?.CurrentBed()?.GetUniqueLoadID() };
            }, cancellationToken);
        }
    }
}
