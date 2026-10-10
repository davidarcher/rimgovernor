using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimGovernor.Runtime;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Stages one open-air training range column for the native training job (#2610,
    // #2686): a stand and a dummy RangeDistance cells north of it and two sentinel
    // walls well off the line, no walls or partitions, with a low-skill
    // violence-capable colonist whose only work is training. Options (#2688) add a
    // second column beside the first with its own shooter, an idle drafted
    // bystander standing mid-lane, and a starting Shooting level.
    public sealed class TrainingFixture
    {
        // The shared "trained with" thought (#2710) the pawn holds now.
        public static bool HasTrainedWith(Pawn p) =>
            p.needs?.mood?.thoughts?.memories?.GetFirstMemoryOfDef(DefDatabase<ThoughtDef>.GetNamed(TrainingCompany.ThoughtName)) != null;

        private const int RangeDistance = 8; // go/internal/policy.RangeDistance

        [Tool("test/training_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Build one open-air training range column at the cell and stage a low-skill colonist whose only work is training. Test builds only.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, int x, int z,
            [ToolParameter(Description = "Starting Shooting level of every shooter (default 0).")] int level = 0,
            [ToolParameter(Description = "Stage a second column at x+1 with its own shooter.")] bool neighbour = false,
            [ToolParameter(Description = "Stand a drafted idle colonist mid-lane, between the stand and the dummy.")] bool bystander = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var capable = map.mapPawns.FreeColonistsSpawned.Where(p => !p.WorkTagIsDisabled(WorkTags.Violent)
                    && !(p.story?.traits?.HasTrait(TraitDefOf.Brawler) ?? false)
                    && !p.skills.GetSkill(SkillDefOf.Shooting).TotallyDisabled).ToList();
                var needed = 1 + (neighbour ? 1 : 0) + (bystander ? 1 : 0);
                if (capable.Count < needed) throw new InvalidOperationException("Need " + needed + " violence-capable colonists.");
                var pawn = capable[0];
                var second = neighbour ? capable[1] : null;
                var idle = bystander ? capable[capable.Count - 1] : null;

                Thing Put(string defName, int cx, int cz)
                {
                    var def = DefDatabase<ThingDef>.GetNamed(defName);
                    var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.WoodLog : null);
                    thing.SetFaction(Faction.OfPlayer);
                    return GenSpawn.Spawn(thing, new IntVec3(cx, 0, cz), map);
                }
                var stand = Put("RimGovernor_TrainingBowStand", x, z);
                var dummy = Put("RimGovernor_TrainingDummy", x, z + RangeDistance);
                Thing secondDummy = null;
                if (neighbour)
                {
                    Put("RimGovernor_TrainingBowStand", x + 1, z);
                    secondDummy = Put("RimGovernor_TrainingDummy", x + 1, z + RangeDistance);
                }
                var sentinels = new List<Thing> { Put("Wall", x + 8, z + 6), Put("Wall", x - 8, z + 6) };

                void Teleport(Pawn p, int px, int pz)
                {
                    p.drafter.Drafted = false;
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    p.Position = new IntVec3(px, 0, pz);
                    p.Notify_Teleported(true, true);
                }
                void Shooter(Pawn p, int px, int pz)
                {
                    Teleport(p, px, pz);
                    foreach (var def in new[] { SkillDefOf.Shooting, SkillDefOf.Melee })
                    {
                        var record = p.skills.GetSkill(def);
                        record.Level = def == SkillDefOf.Shooting ? level : 0;
                        record.xpSinceLastLevel = 0f;
                        record.xpSinceMidnight = 0f;
                        record.passion = Passion.Minor;
                    }
                    // A "trained with" memory from an earlier case would outlast the restage.
                    p.needs?.mood?.thoughts?.memories?.RemoveMemoriesOfDef(DefDatabase<ThoughtDef>.GetNamed(TrainingCompany.ThoughtName));
                    p.equipment.DestroyAllEquipment();
                    p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_Autopistol")));
                    p.workSettings.EnableAndInitialize();
                    var training = DefDatabase<WorkTypeDef>.GetNamed("RimGovernorTraining");
                    foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading) p.workSettings.SetPriority(work, work == training ? 1 : 0);
                }
                Shooter(pawn, x + 3, z - 2);
                if (second != null) Shooter(second, x + 4, z - 2);
                if (idle != null)
                {
                    // Drafted and idle: it holds its cell in the line of fire.
                    Teleport(idle, x, z + RangeDistance / 2);
                    foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading) idle.workSettings.SetPriority(work, 0);
                    idle.drafter.Drafted = true;
                }
                return new
                {
                    success = true, pawn = pawn.GetUniqueLoadID(), stand = stand.GetUniqueLoadID(), dummy = dummy.GetUniqueLoadID(),
                    weapon = "Gun_Autopistol", dummyMax = dummy.MaxHitPoints,
                    neighbour = second?.GetUniqueLoadID() ?? "", neighbourDummy = secondDummy?.GetUniqueLoadID() ?? "",
                    bystander = idle?.GetUniqueLoadID() ?? "",
                    sentinels = string.Join(",", sentinels.Select(t => t.GetUniqueLoadID())),
                };
            }, cancellationToken);
        }

        [Tool("test/training_act", Description = "UNSAFE FOR MODEL EXECUTION. One disturbance for the training fixture: finish a research project, set a Shooting level, light a fire on a thing, draft a pawn, or have one pawn chat with another. Test builds only.")]
        public async Task<object> Act(IRimBridgeContext ctx, CancellationToken cancellationToken, string action,
            string pawnId = "", string otherId = "", string thingId = "", string research = "", int level = 0)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                Pawn Pawn(string id) => map.mapPawns.AllPawnsSpawned.Single(p => p.GetUniqueLoadID() == id);
                switch (action)
                {
                    case "research":
                        var project = DefDatabase<ResearchProjectDef>.GetNamed(research);
                        Find.ResearchManager.FinishProject(project, doCompletionDialog: false, researcher: null, doCompletionLetter: false);
                        return new { success = project.IsFinished };
                    case "level":
                        var record = Pawn(pawnId).skills.GetSkill(SkillDefOf.Shooting);
                        record.Level = level;
                        record.xpSinceLastLevel = 0f;
                        record.xpSinceMidnight = 0f;
                        return new { success = true };
                    case "fire":
                        var thing = map.listerThings.AllThings.Single(t => t.GetUniqueLoadID() == thingId);
                        GenSpawn.Spawn(ThingDefOf.Fire, thing.Position, map);
                        return new { success = true };
                    case "draft":
                        Pawn(pawnId).drafter.Drafted = true;
                        return new { success = true };
                    case "chat":
                        var a = Pawn(pawnId);
                        var b = Pawn(otherId);
                        // Vanilla's chat rule: within 6 cells with a line of sight, whatever the job.
                        var good = SocialInteractionUtility.IsGoodPositionForInteraction(a.Position, b.Position, map);
                        var can = SocialInteractionUtility.CanInitiateRandomInteraction(a) && SocialInteractionUtility.CanReceiveRandomInteraction(b);
                        var chatted = good && can && a.interactions.TryInteractWith(b, InteractionDefOf.Chitchat);
                        return new { success = true, good, can, chatted, distance = a.Position.DistanceTo(b.Position), jobA = a.CurJob?.def.defName, jobB = b.CurJob?.def.defName };
                    default:
                        throw new InvalidOperationException("Unknown training action " + action);
                }
            }, cancellationToken);
        }

        [Tool("test/training_inspect", Description = "Read-only postcondition of the training fixture: the colonist's skills, hands, job and the range's hit points.")]
        public async Task<object> Inspect(IRimBridgeContext ctx, CancellationToken cancellationToken, string pawnId, string dummyId, string sentinels,
            string otherId = "", string bystanderId = "")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var pawn = map.mapPawns.AllPawnsSpawned.Single(p => p.GetUniqueLoadID() == pawnId);
                Pawn Optional(string id) => string.IsNullOrEmpty(id) ? null : map.mapPawns.AllPawnsSpawned.Single(p => p.GetUniqueLoadID() == id);
                var other = Optional(otherId);
                var idle = Optional(bystanderId);
                Thing[] Things(string csv)
                {
                    var ids = csv.Split(',');
                    return map.listerThings.AllThings.Where(t => ids.Contains(t.GetUniqueLoadID())).ToArray();
                }
                int Hp(Thing[] things) => things.Sum(t => t.HitPoints);
                var dummy = Things(dummyId);
                var practice = new[] { "Bow_Training", "Gun_PracticeRifle", "Gun_PracticePulseRifle", "Gun_PracticeBeamEmitter" };
                var held = pawn.equipment.AllEquipmentListForReading.Select(t => t.def.defName).Concat(pawn.inventory.innerContainer.Select(t => t.def.defName)).ToArray();
                var shooting = pawn.skills.GetSkill(SkillDefOf.Shooting);
                return new
                {
                    success = true, tick = Find.TickManager.TicksGame,
                    shootingLevel = shooting.Level, shootingXp = shooting.xpSinceLastLevel, shootingMidnightXp = shooting.xpSinceMidnight,
                    primary = pawn.equipment.Primary?.def.defName, held, practiceOnMap = map.listerThings.AllThings.Count(t => practice.Contains(t.def.defName)),
                    job = pawn.CurJob?.def.defName, shotsFired = pawn.records.GetAsInt(RecordDefOf.ShotsFired),
                    drafted = pawn.Drafted,
                    dummyHp = dummy.Length == 0 ? 0 : dummy[0].HitPoints,
                    sentinelHp = Hp(Things(sentinels)), sentinelMax = Things(sentinels).Sum(t => t.MaxHitPoints),
                    x = pawn.Position.x, z = pawn.Position.z,
                    trainedWith = HasTrainedWith(pawn), otherTrainedWith = other != null && HasTrainedWith(other),
                    otherJob = other?.CurJob?.def.defName, otherShots = other?.records.GetAsInt(RecordDefOf.ShotsFired) ?? 0,
                    bystanderHurt = idle == null ? 0 : idle.health.hediffSet.hediffs.Count(h => h is Hediff_Injury),
                    bystanderHealth = idle?.health.summaryHealth.SummaryHealthPercent ?? 1f,
                    bystanderX = idle?.Position.x ?? 0, bystanderZ = idle?.Position.z ?? 0,
                };
            }, cancellationToken);
        }
    }
}
