using System;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimGovernor.Runtime;
using RimWorld;
using Verse;
using Verse.AI;
using Fx = RimGovernor.Fixtures.Contracts;

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

        private const int RangeDistance = 8; // Brain.Policy RangeDistance

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
                Put("RimGovernor_TrainingBowStand", x, z);
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
                return FixtureKit.Reply(new Fx.TrainingPrepared
                {
                    Pawn = pawn.GetUniqueLoadID(), Dummy = dummy.GetUniqueLoadID(),
                    Weapon = "Gun_Autopistol", DummyMax = dummy.MaxHitPoints,
                    Neighbour = second?.GetUniqueLoadID() ?? "", NeighbourDummy = secondDummy?.GetUniqueLoadID() ?? "",
                    Bystander = idle?.GetUniqueLoadID() ?? "",
                    Sentinels = string.Join(",", sentinels.Select(t => t.GetUniqueLoadID())),
                });
            }, cancellationToken);
        }

        [Tool("test/training_act", Description = "UNSAFE FOR MODEL EXECUTION. One disturbance for the training fixture: set a Shooting level, light a fire on a thing, draft a pawn, or have one pawn chat with another. Test builds only.")]
        public async Task<object> Act(IRimBridgeContext ctx, CancellationToken cancellationToken, string action,
            string pawnId = "", string otherId = "", string thingId = "", int level = 0)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                Pawn Pawn(string id) => map.mapPawns.AllPawnsSpawned.Single(p => p.GetUniqueLoadID() == id);
                switch (action)
                {
                    case "level":
                        var record = Pawn(pawnId).skills.GetSkill(SkillDefOf.Shooting);
                        record.Level = level;
                        record.xpSinceLastLevel = 0f;
                        record.xpSinceMidnight = 0f;
                        return FixtureKit.Reply(new Fx.FixtureDone { Success = true });
                    case "fire":
                        var thing = map.listerThings.AllThings.Single(t => t.GetUniqueLoadID() == thingId);
                        GenSpawn.Spawn(ThingDefOf.Fire, thing.Position, map);
                        return FixtureKit.Reply(new Fx.FixtureDone { Success = true });
                    case "draft":
                        Pawn(pawnId).drafter.Drafted = true;
                        return FixtureKit.Reply(new Fx.FixtureDone { Success = true });
                    case "chat":
                        var a = Pawn(pawnId);
                        var b = Pawn(otherId);
                        // Vanilla's chat rule: within 6 cells with a line of sight, whatever the job.
                        var good = SocialInteractionUtility.IsGoodPositionForInteraction(a.Position, b.Position, map);
                        var can = SocialInteractionUtility.CanInitiateRandomInteraction(a) && SocialInteractionUtility.CanReceiveRandomInteraction(b);
                        var chatted = good && can && a.interactions.TryInteractWith(b, InteractionDefOf.Chitchat);
                        return FixtureKit.Reply(new Fx.TrainingChat { Good = good, Chatted = chatted });
                    default:
                        throw new InvalidOperationException("Unknown training action " + action);
                }
            }, cancellationToken);
        }

        [Tool("test/skill_learning_verify", Description = "Test-only read observer of recipe, surgery, tend and sparring XP callbacks; start, read or stop verification, never issue pawn work.")]
        public async Task<object> VerifyLearning(IRimBridgeContext ctx, CancellationToken cancellationToken, string action)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (action == "start") SkillLearningVerifier.Start();
                else if (action == "stop") SkillLearningVerifier.Stop();
                else if (action != "read") throw new ArgumentException("Expected start, read or stop.");
                return FixtureKit.Reply(new Fx.SkillLearningVerifyRead { Recipe = SkillLearningVerifier.Recipes, Sparring = SkillLearningVerifier.Sparring,
                    Surgery = SkillLearningVerifier.Surgery, Tend = SkillLearningVerifier.Tend, Failures = SkillLearningVerifier.Failures.ToArray() });
            }, cancellationToken);
        }

        // #3632: one doctor and one patient lying in a medical bed. "tend" cuts the patient and hands the doctor the native
        // TendPatient job without medicine. "surgery" takes a leg and adds the native peg-leg bill with its ingredients beside
        // the bed; "poll" hands the doctor the native DoBill job (built as WorkGiver_DoBill builds it) once the patient lies in bed.
        // Whoever then does the work, the verifier checks each Medicine callback.
        private static Pawn medicalDoctor, medicalPatient;
        private static Bill_Medical medicalBill;
        private static List<Thing> medicalInputs = new List<Thing>();
        private static bool medicalStarted;

        [Tool("test/medical_learning", Description = "UNSAFE FOR MODEL EXECUTION. Stage a doctor and a patient in bed for one native tend or surgery; poll hands the doctor the staged surgery once the patient lies in bed. Test builds only.")]
        public async Task<object> MedicalLearning(IRimBridgeContext ctx, CancellationToken cancellationToken, string action)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (action == "poll")
                {
                    if (medicalBill != null && !medicalStarted && medicalPatient.InBed())
                    {
                        var job = JobMaker.MakeJob(JobDefOf.DoBill, medicalPatient);
                        job.bill = medicalBill;
                        job.targetQueueB = medicalInputs.Select(t => new LocalTargetInfo(t)).ToList();
                        job.countQueue = medicalInputs.Select(t => t.stackCount).ToList();
                        job.haulMode = HaulMode.ToCellNonStorage;
                        job.workGiverDef = DefDatabase<WorkGiverDef>.AllDefsListForReading.FirstOrDefault(d => d.Worker is WorkGiver_DoBill g && g.ThingIsUsableBillGiver(medicalPatient));
                        medicalStarted = medicalDoctor.jobs.TryTakeOrderedJob(job);
                    }
                    return MedicalRead();
                }
                if (action != "tend" && action != "surgery") throw new ArgumentException("Expected tend, surgery or poll.");
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var pawns = map.mapPawns.FreeColonistsSpawned.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)
                    && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)).OrderBy(p => p.thingIDNumber).ToList();
                if (pawns.Count < 2) throw new InvalidOperationException("Need two colonists able to doctor.");
                medicalDoctor = pawns[0]; medicalPatient = pawns[1]; medicalBill = null; medicalInputs = new List<Thing>(); medicalStarted = false;
                foreach (var p in new[] { medicalDoctor, medicalPatient })
                {
                    p.drafter.Drafted = false;
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(w => !p.WorkTypeIsDisabled(w))) p.workSettings.SetPriority(work, 0);
                }
                // A surgery patient keeps to its bed only through its Patient work (WorkGiver_PatientGoToBedTreatment), which
                // also needs a doctor with Doctor work active (AnyAvailableDoctorFor); without both, the patient's next job check
                // gets it up and the doctor's DoBill job fails on Pawn.CurrentlyUsableForBills.
                if (action == "surgery")
                {
                    medicalDoctor.workSettings.SetPriority(WorkTypeDefOf.Doctor, 1);
                    medicalPatient.workSettings.SetPriority(DefDatabase<WorkGiverDef>.AllDefsListForReading
                        .First(d => d.giverClass == typeof(WorkGiver_PatientGoToBedTreatment)).workType, 1);
                }
                var origin = medicalDoctor.Position;
                var area = new CellRect(origin.x + 1, origin.z - 1, 3, 4);
                foreach (var cell in area.Cells)
                {
                    foreach (var thing in cell.GetThingList(map).Where(t => !(t is Pawn)).ToList()) thing.Destroy();
                    map.terrainGrid.SetTerrain(cell, TerrainDefOf.Concrete);
                }
                var bed = (Building_Bed)ThingMaker.MakeThing(ThingDefOf.Bed, ThingDefOf.WoodLog);
                bed.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(bed, new IntVec3(origin.x + 2, 0, origin.z), map);
                bed.Medical = true;
                medicalPatient.playerSettings.medCare = MedicalCareCategory.Best;
                medicalPatient.Position = bed.Position;
                medicalPatient.Notify_Teleported(true, true);
                if (action == "tend")
                {
                    var torso = medicalPatient.health.hediffSet.GetNotMissingParts().First(p => p.def == BodyPartDefOf.Torso);
                    medicalPatient.TakeDamage(new DamageInfo(DamageDefOf.Cut, 5f, 999f, -1, null, torso));
                    if (!medicalPatient.health.HasHediffsNeedingTend()) throw new InvalidOperationException("Fixture cut requires tending.");
                }
                else
                {
                    var leg = medicalPatient.health.hediffSet.GetNotMissingParts().First(p => p.def == BodyPartDefOf.Leg);
                    medicalPatient.health.AddHediff(HediffMaker.MakeHediff(HediffDefOf.MissingBodyPart, medicalPatient, leg), leg);
                    // The epic's practice surgery; no typed route to a recipe def that is not in a DefOf.
                    var recipe = DefDatabase<RecipeDef>.GetNamed("InstallPegLeg");
                    medicalBill = HealthCardUtility.CreateSurgeryBill(medicalPatient, recipe, leg, null, false);
                    var spot = new IntVec3(origin.x + 1, 0, origin.z);
                    foreach (var ingredient in recipe.ingredients)
                    {
                        var allowed = ingredient.filter.AllowedThingDefs.Where(d => medicalBill.IsFixedOrAllowedIngredient(d)).ToList();
                        var def = allowed.Contains(ThingDefOf.MedicineHerbal) ? ThingDefOf.MedicineHerbal : allowed.First();
                        var input = ThingMaker.MakeThing(def);
                        input.stackCount = ingredient.CountRequiredOfFor(def, recipe, medicalBill);
                        medicalInputs.Add(GenSpawn.Spawn(input, spot, map));
                        spot += IntVec3.North;
                    }
                }
                medicalPatient.jobs.StartJob(JobMaker.MakeJob(JobDefOf.LayDown, bed), JobCondition.InterruptForced);
                if (action == "tend") medicalStarted = medicalDoctor.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.TendPatient, medicalPatient));
                return MedicalRead();
            }, cancellationToken);
        }

        private static object MedicalRead() => FixtureKit.Reply(new Fx.MedicalLearningRead
        {
            InBed = medicalPatient?.InBed() ?? false, Started = medicalStarted,
        });

        [Tool("test/skill_learning_probe", Description = "UNSAFE FOR MODEL EXECUTION. Compare bounded learning projections against native SkillRecord methods on detached records. Test builds only.")]
        public async Task<object> LearningProbe(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var pawn = Find.CurrentMap.mapPawns.FreeColonistsSpawned.First(p => p.skills != null);
                var failures = new List<string>();
                var samples = 0;
                foreach (var def in new[] { SkillDefOf.Cooking, SkillDefOf.Melee, SkillDefOf.Shooting, SkillDefOf.Intellectual, SkillDefOf.Plants })
                foreach (var passion in new[] { Passion.None, Passion.Minor, Passion.Major })
                foreach (var saturated in new[] { false, true })
                {
                    var actual = new SkillRecord(pawn, def) { levelInt = 10, passion = passion,
                        xpSinceLastLevel = 100, xpSinceMidnight = saturated ? 5000 : 0 };
                    var before = pawn.skills.GetSkill(def);
                    var original = (before.levelInt, before.xpSinceLastLevel, before.xpSinceMidnight);
                    var evidence = NativeSkillLearning.Read(pawn, actual) with {
                        ValidForTicks = RimGovernor.Brain.Domain.Fact.Known(1000),
                        TrackerIntervalTicks = 1, TrackerPhase = 0, NextMidnightReset = default,
                        NextDecay = RimGovernor.Brain.Domain.Fact.Known(200),
                        PracticeEligible = RimGovernor.Brain.Domain.Fact.Known(true),
                        PracticeCeiling = RimGovernor.Brain.Domain.Fact.Known(SkillRecord.MaxLevel),
                    };
                    var direct = def == SkillDefOf.Shooting;
                    var path = direct ? RimGovernor.Brain.Policy.SkillXpPath.Range : RimGovernor.Brain.Policy.SkillXpPath.NormalRecord;
                    var during = def == SkillDefOf.Intellectual || def == SkillDefOf.Plants;
                    var raw = def == SkillDefOf.Plants ? .085f : .1f;
                    if (during) evidence = evidence with { NextMidnightReset = RimGovernor.Brain.Domain.Fact.Known(100) };
                    var callback = during
                        ? new RimGovernor.Brain.Policy.SkillXpEvent("callback", 1, RimGovernor.Brain.Domain.Fact.Known((double)raw), path, BeforeDecay: true, Count: 200, Interval: 1)
                        : new RimGovernor.Brain.Policy.SkillXpEvent("callback", 1, RimGovernor.Brain.Domain.Fact.Known(10d), path);
                    var prediction = RimGovernor.Brain.Policy.ForecastSkillPolicy.ForecastSkill(evidence, new[] { callback }, 200);
                    if (during)
                        for (var tick = 1; tick <= 200; tick++)
                        {
                            actual.Learn(raw);
                            if (tick == 100) actual.xpSinceMidnight = 0;
                            if (tick == 200) actual.Interval();
                        }
                    else { actual.Learn(10, direct); actual.Interval(); }
                    samples++;
                    if (!prediction.Xp.IsKnown || Math.Abs(prediction.Xp.Value - actual.xpSinceLastLevel) > .01
                        || prediction.StoredLevel.Value != actual.levelInt)
                        failures.Add(def.defName + "/" + passion + "/" + saturated);
                    if (original != (before.levelInt, before.xpSinceLastLevel, before.xpSinceMidnight)) failures.Add("read_mutated_pawn");
                }
                return FixtureKit.Reply(new Fx.SkillLearningProbeRead { Samples = samples, Failures = failures });
            }, cancellationToken);
        }
        [Tool("test/skill_stat_curve_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Clear two Medicine-capable colonists' hediffs and inspiration, compare the first's biography medical stat curves with live native stat calls at each level (temporarily set and restored), then anesthetize the second so its pipeline disqualifies. Test builds only.")]
        public async Task<object> SkillStatCurvePrepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = FixtureKit.CurrentMap();
                var pawns = map.mapPawns.FreeColonistsSpawned.Where(p => p.skills?.GetSkill(SkillDefOf.Medicine).TotallyDisabled == false).Take(2).ToArray();
                if (pawns.Length < 2) throw new InvalidOperationException("Two Medicine-capable colonists are required.");
                foreach (var pawn in pawns)
                {
                    foreach (var hediff in pawn.health.hediffSet.hediffs.ToList()) pawn.health.RemoveHediff(hediff);
                    if (pawn.Inspired) pawn.mindState.inspirationHandler.EndInspiration(pawn.Inspiration);
                }
                var (qualified, disqualified) = (pawns[0], pawns[1]);
                var skill = qualified.skills.GetSkill(SkillDefOf.Medicine);
                var before = (skill.levelInt, skill.xpSinceLastLevel, skill.xpSinceMidnight);
                var biography = NativePawnDetails.Biography(qualified);
                var failures = biography.SkillStatFindings.ToList();
                if (before != (skill.levelInt, skill.xpSinceLastLevel, skill.xpSinceMidnight)) failures.Add("read_mutated_pawn");
                var samples = 0;
                try
                {
                    foreach (var curve in biography.SkillStatCurves)
                    {
                        var stat = DefDatabase<StatDef>.GetNamed(curve.Stat); // the curve names its stat by defName across the wire
                        foreach (var level in curve.Levels)
                        {
                            // Disposable fixture mutation only: evaluate the actual native level without stat caching.
                            skill.levelInt = level.StoredLevel;
                            var actual = stat.Worker.GetValue(StatRequest.For(qualified));
                            samples++;
                            if (Math.Abs(level.Value - actual) > 1e-4 * Math.Max(1, Math.Abs(actual)))
                                failures.Add($"{curve.Stat}/{level.StoredLevel}: {level.Value} vs {actual}");
                        }
                    }
                }
                finally
                {
                    skill.levelInt = before.levelInt;
                    skill.xpSinceLastLevel = before.xpSinceLastLevel;
                    skill.xpSinceMidnight = before.xpSinceMidnight;
                }
                disqualified.health.AddHediff(HediffDefOf.Anesthetic);
                return FixtureKit.Reply(new Fx.SkillStatCurvePrepared { Qualified = qualified.GetUniqueLoadID(), Disqualified = disqualified.GetUniqueLoadID(),
                    Samples = samples, Failures = failures });
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
                var before = (shooting.levelInt, shooting.xpSinceLastLevel, shooting.xpSinceMidnight);
                var learning = NativeSkillLearning.Read(pawn, shooting);
                var readOnly = before == (shooting.levelInt, shooting.xpSinceLastLevel, shooting.xpSinceMidnight);
                // This fixture starts at level zero with no XP; under level ten there is no native decay.
                // Count completed shots, never installation or elapsed time, as learning callbacks.
                var shots = pawn.records.GetAsInt(RecordDefOf.ShotsFired);
                var baseline = learning with {
                    StoredLevel = RimGovernor.Brain.Domain.Fact.Known(0), Xp = RimGovernor.Brain.Domain.Fact.Known(0d),
                    XpSinceMidnight = RimGovernor.Brain.Domain.Fact.Known(0d),
                    PracticeEligible = RimGovernor.Brain.Domain.Fact.Known(true),
                    RangeAppliedBudget = RimGovernor.Brain.Domain.Fact.Known((double)RangeTraining.DailyXpBudget),
                    ValidForTicks = RimGovernor.Brain.Domain.Fact.Known(2),
                };
                var events = Enumerable.Range(0, shots).Select(i => new RimGovernor.Brain.Policy.SkillXpEvent(
                    i.ToString(), 1, RimGovernor.Brain.Domain.Fact.Known((double)RangeTraining.ShotXp(RangeTraining.UnlockedWeapon())), RimGovernor.Brain.Policy.SkillXpPath.Range)).ToArray();
                var projected = RimGovernor.Brain.Policy.ForecastSkillPolicy.ForecastSkill(baseline, events, 1);
                return FixtureKit.Reply(new Fx.TrainingRead
                {
                    LearningReadOnly = readOnly, ProjectedLevel = projected.StoredLevel.ValueOrDefault,
                    ProjectedXp = projected.Xp.ValueOrDefault, ProjectionKnown = projected.Xp.IsKnown,
                    Tick = Find.TickManager.TicksGame,
                    ShootingLevel = shooting.Level, ShootingXp = shooting.xpSinceLastLevel, ShootingMidnightXp = shooting.xpSinceMidnight,
                    Primary = pawn.equipment.Primary?.def.defName ?? "", Held = held, PracticeOnMap = map.listerThings.AllThings.Count(t => practice.Contains(t.def.defName)),
                    Job = pawn.CurJob?.def.defName ?? "", ShotsFired = shots,
                    DummyHp = dummy.Length == 0 ? 0 : dummy[0].HitPoints,
                    SentinelHp = Hp(Things(sentinels)), SentinelMax = Things(sentinels).Sum(t => t.MaxHitPoints),
                    TrainedWith = HasTrainedWith(pawn),
                    OtherJob = other?.CurJob?.def.defName ?? "",
                    BystanderHurt = idle == null ? 0 : idle.health.hediffSet.hediffs.Count(h => h is Hediff_Injury),
                    BystanderHealth = idle?.health.summaryHealth.SummaryHealthPercent ?? 1f,
                });
            }, cancellationToken);
        }
    }
}

namespace HomeBridge.BridgeTools
{
    // Test builds only. Observe actual callbacks, and predict from independently quoted job mechanics.
    internal static class SkillLearningVerifier
    {
        private static readonly Harmony Harmony = new Harmony("rimgovernor.test.learning-verifier");
        private static bool installed, enabled, busy;
        internal static int Recipes, Sparring, Surgery, Tend;
        internal static readonly List<string> Failures = new List<string>();
        internal sealed class Check
        {
            internal RimGovernor.Brain.Policy.SkillProjection Prediction;
            internal string Kind;
        }
        internal static void Start()
        {
            Recipes = Sparring = Surgery = Tend = 0; Failures.Clear(); enabled = true;
            if (installed) return;
            Harmony.Patch(AccessTools.Method(typeof(SkillRecord), nameof(SkillRecord.Learn)),
                prefix: new HarmonyMethod(typeof(SkillLearningVerifier), nameof(Before)),
                postfix: new HarmonyMethod(typeof(SkillLearningVerifier), nameof(After)));
            installed = true;
        }
        internal static void Stop() { enabled = false; }
        private static void Before(SkillRecord __instance, float xp, bool direct, bool ignoreLearnRate, out Check __state)
        {
            __state = null;
            if (!enabled || busy || xp <= 0 || direct || ignoreLearnRate) return;
            var pawn = __instance.Pawn;
            var recipe = pawn.CurJob?.bill?.recipe;
            var kind = "";
            double raw = 0;
            var path = RimGovernor.Brain.Policy.SkillXpPath.NormalRecord;
            if (__instance.def == SkillDefOf.Medicine && pawn.CurJob?.targetA.Pawn is Pawn patient
                && (pawn.CurJob.def == JobDefOf.TendPatient || pawn.jobs.curDriver is JobDriver_GovernorTend))
            {
                // Toils_Tend.FinalizeTend learns through Pawn_SkillTracker.Learn with the job's medicine, before the treatment.
                raw = RimGovernor.Brain.Bridge.DefinitionCatalog.ObserveTendLearning(patient.RaceProps.Animal, pawn.CurJob.targetB.Thing?.def);
                kind = "tend";
                path = RimGovernor.Brain.Policy.SkillXpPath.NormalTracker;
            }
            else if (pawn.CurJob?.def.defName == SparringBouts.JobName && __instance.def == SkillDefOf.Melee)
            {
                var verb = pawn.equipment.PrimaryEq?.PrimaryVerb;
                if (verb == null) { Failures.Add("missing_sparring_verb"); return; }
                if (verb is not Verb_MeleeAttack) { Failures.Add("sparring_quote_unknown"); return; }
                // Native quotes one exchange as 200 XP per second of the verb's full cycle, the quote the brain plans with.
                raw = 200d * verb.verbProps.AdjustedFullCycleTime(verb, pawn);
                kind = "sparring";
            }
            else if (recipe != null && recipe.workSkill == __instance.def && !recipe.UsesUnfinishedThing
                && pawn.jobs.curDriver is JobDriver_DoBill driver)
            {
                var quote = RimGovernor.Brain.Bridge.DefinitionCatalog.ObserveRecipeLearning(recipe);
                if (!quote.RawXpPerWorkTick.IsKnown) return;
                raw = quote.RawXpPerWorkTick.Value * driver.ticksSpentDoingRecipeWork;
                kind = recipe.IsSurgery ? "surgery" : "recipe";
            }
            else return;
            busy = true;
            try
            {
                var evidence = NativeSkillLearning.Read(pawn, __instance) with {
                    ValidForTicks = RimGovernor.Brain.Domain.Fact.Known(2), NextDecay = RimGovernor.Brain.Domain.Fact.Known(200) };
                var predicted = RimGovernor.Brain.Policy.ForecastSkillPolicy.ForecastSkill(evidence,
                    new[] { new RimGovernor.Brain.Policy.SkillXpEvent("native-callback", 0,
                        RimGovernor.Brain.Domain.Fact.Known(raw), path) }, 0);
                if (Math.Abs(raw - xp) > .01) Failures.Add(kind + "_raw_xp_quote");
                __state = new Check { Prediction = predicted, Kind = kind };
            }
            finally { busy = false; }
        }
        private static void After(SkillRecord __instance, Check __state)
        {
            if (__state == null) return;
            switch (__state.Kind)
            {
                case "recipe": Recipes++; break;
                case "surgery": Surgery++; break;
                case "tend": Tend++; break;
                default: Sparring++; break;
            }
            var predicted = __state.Prediction;
            if (!predicted.Xp.IsKnown || predicted.StoredLevel.Value != __instance.levelInt
                || Math.Abs(predicted.Xp.Value - __instance.xpSinceLastLevel) > .1)
                Failures.Add(__state.Kind + "_projected_state");
        }
    }
}
