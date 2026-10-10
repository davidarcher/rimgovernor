using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Stages one training range lane for the native training job (#2610): a stand,
    // a dummy 11 cells north, a partition column either side, a back wall and two
    // sentinel walls well off the lane, with a low-skill violence-capable colonist
    // whose only work is training.
    public sealed class TrainingFixture
    {
        private const int LaneLength = 12;

        [Tool("test/training_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Build one training range lane around the cell and stage a low-skill colonist whose only work is training. skill is shooting or melee. Test builds only.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, int x, int z, string skill)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var melee = skill == "melee";
                var pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !p.WorkTagIsDisabled(WorkTags.Violent)
                    && !(p.story?.traits?.HasTrait(TraitDefOf.Brawler) ?? false)
                    && !p.skills.GetSkill(SkillDefOf.Shooting).TotallyDisabled && !p.skills.GetSkill(SkillDefOf.Melee).TotallyDisabled);
                if (pawn == null) throw new InvalidOperationException("No violence-capable colonist.");

                Thing Put(string defName, int cx, int cz)
                {
                    var def = DefDatabase<ThingDef>.GetNamed(defName);
                    var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.WoodLog : null);
                    thing.SetFaction(Faction.OfPlayer);
                    return GenSpawn.Spawn(thing, new IntVec3(cx, 0, cz), map);
                }
                var stand = Put("RimGovernor_TrainingBowStand", x, z);
                var dummy = Put("RimGovernor_TrainingDummy", x, z + LaneLength - 1);
                var partitions = new List<Thing>(); // the range has none; kept so the case reads an empty set
                var backWall = new List<Thing>();
                for (var dx = -1; dx <= 1; dx++) backWall.Add(Put("Wall", x + dx, z + LaneLength));
                var sentinels = new List<Thing> { Put("Wall", x + 8, z + 6), Put("Wall", x - 8, z + 6) };

                pawn.drafter.Drafted = false;
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                pawn.Position = new IntVec3(x + 3, 0, z - 2);
                pawn.Notify_Teleported(true, true);
                foreach (var def in new[] { SkillDefOf.Shooting, SkillDefOf.Melee })
                {
                    var record = pawn.skills.GetSkill(def);
                    record.Level = melee && def == SkillDefOf.Melee ? 5 : 0;
                    record.xpSinceLastLevel = 0f;
                    record.xpSinceMidnight = 0f;
                    record.passion = Passion.Minor;
                }
                pawn.equipment.DestroyAllEquipment();
                var real = melee
                    ? ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("MeleeWeapon_Knife"), ThingDefOf.Steel)
                    : ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_Autopistol"));
                pawn.equipment.AddEquipment((ThingWithComps)real);
                pawn.workSettings.EnableAndInitialize();
                var training = DefDatabase<WorkTypeDef>.GetNamed("RimGovernorTraining");
                foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading) pawn.workSettings.SetPriority(work, work == training ? 1 : 0);
                return new
                {
                    success = true, pawn = pawn.GetUniqueLoadID(), stand = stand.GetUniqueLoadID(), dummy = dummy.GetUniqueLoadID(),
                    weapon = real.def.defName, dummyMax = dummy.MaxHitPoints,
                    partitionMax = partitions.Sum(t => t.MaxHitPoints), backWallMax = backWall.Sum(t => t.MaxHitPoints),
                    partitions = string.Join(",", partitions.Select(t => t.GetUniqueLoadID())),
                    backWall = string.Join(",", backWall.Select(t => t.GetUniqueLoadID())),
                    sentinels = string.Join(",", sentinels.Select(t => t.GetUniqueLoadID())),
                };
            }, cancellationToken);
        }

        [Tool("test/training_inspect", Description = "Read-only postcondition of the training fixture: the colonist's skills, hands, job and the lane's hit points.")]
        public async Task<object> Inspect(IRimBridgeContext ctx, CancellationToken cancellationToken, string pawnId, string dummyId, string partitions, string backWall, string sentinels)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var pawn = map.mapPawns.AllPawnsSpawned.Single(p => p.GetUniqueLoadID() == pawnId);
                Thing[] Things(string csv)
                {
                    var ids = csv.Split(',');
                    return map.listerThings.AllThings.Where(t => ids.Contains(t.GetUniqueLoadID())).ToArray();
                }
                int Hp(Thing[] things) => things.Sum(t => t.HitPoints);
                var dummy = Things(dummyId);
                var bow = DefDatabase<ThingDef>.GetNamed("Bow_Training");
                var held = pawn.equipment.AllEquipmentListForReading.Select(t => t.def.defName).Concat(pawn.inventory.innerContainer.Select(t => t.def.defName)).ToArray();
                SkillRecord shooting = pawn.skills.GetSkill(SkillDefOf.Shooting), melee = pawn.skills.GetSkill(SkillDefOf.Melee);
                return new
                {
                    success = true, tick = Find.TickManager.TicksGame,
                    shootingLevel = shooting.Level, shootingXp = shooting.xpSinceLastLevel, shootingMidnightXp = shooting.xpSinceMidnight,
                    meleeLevel = melee.Level, meleeXp = melee.xpSinceLastLevel, meleeMidnightXp = melee.xpSinceMidnight,
                    primary = pawn.equipment.Primary?.def.defName, held, bowsOnMap = map.listerThings.ThingsOfDef(bow).Count,
                    job = pawn.CurJob?.def.defName, shotsFired = pawn.records.GetAsInt(RecordDefOf.ShotsFired),
                    dummyHp = dummy.Length == 0 ? 0 : dummy[0].HitPoints,
                    partitionHp = Hp(Things(partitions)), backWallHp = Hp(Things(backWall)), sentinelHp = Hp(Things(sentinels)),
                    sentinelMax = Things(sentinels).Sum(t => t.MaxHitPoints), partitionCount = Things(partitions).Length,
                    x = pawn.Position.x, z = pawn.Position.z,
                };
            }, cancellationToken);
        }
    }
}
