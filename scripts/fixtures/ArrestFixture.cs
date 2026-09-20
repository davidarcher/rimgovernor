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
    public sealed class ArrestFixture
    {
        [Tool("test/arrest_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Stage two disposable colonists and a prisoner bed for native Arrest acceptance.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var hut = FixtureHut.Build(map, 7);
                var pawn = hut.People.First(p => !p.WorkTagIsDisabled(WorkTags.Violent) && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation));
                var target = hut.People.First(p => p != pawn);
                var bed = hut.Interior.SelectMany(c => c.GetThingList(map)).OfType<Building_Bed>().First();
                bed.ForPrisoners = true;
                // The player bed toggle refreshes both caches; setting the
                // flag alone leaves a paused room outside native prison custody.
                bed.GetDistrict().Notify_RoomShapeOrContainedBedsChanged();
                bed.GetRoom().Notify_RoomShapeChanged();
                if (!RestUtility.IsValidBedFor(bed, target, pawn, false, guestStatus: GuestStatus.Prisoner))
                    throw new InvalidOperationException("Fixture prisoner bed must already satisfy native custody eligibility.");
                var ordinary = (Building_Bed)ThingMaker.MakeThing(ThingDefOf.SleepingSpot);
                ordinary.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(ordinary, hut.Door + new IntVec3(2, 0, 0), map);
                pawn.skills.GetSkill(SkillDefOf.Social).Level = 20;
                if (target.GetAcceptArrestChance(pawn) < 1f)
                    throw new InvalidOperationException("Fixture requires a naturally certain arrest with Social 20.");
                return new { success = true, pawn = pawn.GetUniqueLoadID(), target = target.GetUniqueLoadID(),
                    bed = bed.GetUniqueLoadID(), ordinaryBed = ordinary.GetUniqueLoadID() };
            }, cancellationToken);
        }

        [Tool("test/arrest_stage", Description = "UNSAFE FOR MODEL EXECUTION. Stage normal, unarmed, Berserk or legal sad-wander arrest preconditions; never completes an arrest.")]
        public async Task<object> Stage(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string pawnId, string targetId, string scenario)
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
                if (scenario != "unarmed") pawn.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(ThingDefOf.MeleeWeapon_Knife, ThingDefOf.Steel));
                target.drafter.Drafted = false;
                target.jobs.EndCurrentJob(JobCondition.InterruptForced);
                target.needs.rest.CurLevelPercentage = 0.1f;
                if (scenario != "normal")
                {
                    var state = scenario == "berserk" ? MentalStateDefOf.Berserk : MentalStateDefOf.Wander_Sad;
                    if (!target.mindState.mentalStateHandler.TryStartMentalState(state, forced: true, forceWake: true))
                        throw new InvalidOperationException("Fixture mental state did not start.");
                }
                return new { success = true, state = target.MentalStateDef?.defName, armed = pawn.equipment.Primary != null };
            }, cancellationToken);
        }

        [Tool("test/arrest_inspect", Description = "Read-only native custody postcondition for a disposable arrest fixture.")]
        public async Task<object> Inspect(IRimBridgeContext ctx, CancellationToken cancellationToken, string targetId)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var target = Find.CurrentMap.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == targetId);
                return new { success = true, alive = target != null && !target.Dead,
                    mental = target?.InMentalState, prisoner = target?.IsPrisonerOfColony,
                    bed = target?.CurrentBed()?.GetUniqueLoadID(),
                    deadColonists = Find.CurrentMap.mapPawns.AllPawns.Where(p => p.Dead && p.Faction == Faction.OfPlayer).Count() };
            }, cancellationToken);
        }
    }
}
