#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Native;

public sealed partial class PawnControlFixture
{
    private static Pawn? feedingBaby, feedingCarer;
    private static Plant? feedingCrop;
    private static bool feedingPatched, feedingNursing, feedingAdmitted, feedingStaged, feedingReleased, feedingSeen, feedingIntake,
        feedingReturn, feedingProtected, feedingReturnProtected, feedingFinished, feedingReassigned;
    private static string feedingCompetingMethod = "";
    private static double feedingMalnutrition;

    [Tool("test/feeding_response_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Stage one eligible caregiver, a full baby and one small harvest. Hunger is staged once after actual journalled work starts. Never releases work or assigns feeding.")]
    public async Task<object> PrepareFeedingResponse(INativeContext ctx, CancellationToken ct, string carer, string crop, bool nursing) =>
        await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = FixtureKit.PausedMap();
            feedingBaby = null;
            feedingCarer = FixtureKit.Spawned<Pawn>(map, carer);
            feedingCrop = FixtureKit.Spawned<Plant>(map, crop);
            var harvesting = DefDatabase<WorkGiverDef>.AllDefsListForReading.Single(g => g.Worker is WorkGiver_GrowerHarvest);
            if (feedingCarer.WorkTypeIsDisabled(WorkTypeDefOf.Childcare) || feedingCarer.WorkTypeIsDisabled(harvesting.workType))
                throw new InvalidOperationException("The staged carer must be capable of childcare and harvesting.");
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned.ToArray())
            {
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                pawn.needs.food.CurLevelPercentage = 1; pawn.needs.rest.CurLevelPercentage = 1;
                pawn.drafter.Drafted = pawn != feedingCarer;
                foreach (var type in DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(t => !pawn.WorkTypeIsDisabled(t)))
                    pawn.workSettings.SetPriority(type, 0);
                if (pawn != feedingCarer) continue;
                // Only the staged shared Method starts this harvest. Childcare remains native.
                pawn.workSettings.SetPriority(WorkTypeDefOf.Childcare, 1);
                for (var h = 0; h < 24; h++) pawn.timetable.SetAssignment(h, TimeAssignmentDefOf.Anything);
            }
            feedingNursing = nursing;
            feedingCarer.gender = nursing ? Gender.Female : Gender.Male;
            foreach (var lactating in feedingCarer.health.hediffSet.hediffs.Where(h => h.def == HediffDefOf.Lactating).ToArray())
                feedingCarer.health.RemoveHediff(lactating);
            if (nursing)
            {
                var milk = feedingCarer.health.AddHediff(HediffDefOf.Lactating).TryGetComp<HediffComp_Chargeable>()
                    ?? throw new InvalidOperationException("Lactation charge is missing.");
                milk.TryCharge(milk.Props.fullChargeAmount);
            }
            var baby = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                forceGenerateNewPawn: true, canGeneratePawnRelations: false, allowAddictions: false, developmentalStages: DevelopmentalStage.Baby));
            baby.ageTracker.AgeBiologicalTicks = (long)(GenDate.TicksPerYear * 1.5);
            baby.ageTracker.AgeChronologicalTicks = baby.ageTracker.AgeBiologicalTicks;
            GenSpawn.Spawn(baby, CellFinder.RandomClosewalkCellNear(feedingCarer.Position, map, 3), map);
            baby.needs.food.CurLevelPercentage = 1;
            feedingAdmitted = feedingStaged = feedingReleased = feedingSeen = feedingIntake = feedingReturn = feedingProtected
                = feedingReturnProtected = feedingFinished = feedingReassigned = false;
            feedingCompetingMethod = ""; feedingMalnutrition = 0;
            if (!feedingPatched)
            {
                var harmony = new Harmony("rimgovernor.fixture.feeding-response");
                harmony.Patch(AccessTools.Method(typeof(Pawn_JobTracker), "JobTrackerTick"),
                    postfix: new HarmonyMethod(typeof(PawnControlFixture), nameof(FeedingResponseTick)));
                feedingPatched = true;
            }
            feedingBaby = baby;
            return FixtureKit.Reply(FeedingResponseRead());
        }, ct).ConfigureAwait(false);

    private static void FeedingResponseTick(Pawn ___pawn)
    {
        if (feedingBaby is not { Dead: false } baby || feedingCarer is not { } carer || ___pawn != carer) return;
        var owned = NativePawnActivities.Read(carer);
        if (!feedingStaged && owned is not null && carer.CurJob?.def == JobDefOf.Harvest)
        {
            feedingCompetingMethod = owned.Method;
            // The only post-setup mutation stages hunger behind observed governor ownership, once.
            baby.needs.food.CurLevelPercentage = .2f;
            feedingStaged = true;
        }
        if (!feedingStaged) return;
        feedingReleased |= owned == null;
        var feeding = feedingNursing ? carer.jobs.curDriver is JobDriver_Breastfeed : carer.jobs.curDriver is JobDriver_BottleFeedBaby;
        var returning = carer.jobs.curDriver is JobDriver_BringBabyToSafety;
        feedingSeen |= feeding;
        feedingReturn |= feedingSeen && returning;
        feedingIntake |= baby.needs.food.CurLevelPercentage > .21f;
        feedingMalnutrition = Math.Max(feedingMalnutrition, baby.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Malnutrition)?.Severity ?? 0);
        if (NativeBabyCare.Occupied(carer))
        {
            feedingReassigned |= owned != null;
            if (feeding && !feedingProtected || returning && !feedingReturnProtected)
            {
                var context = Context();
                var probe = new PawnActivities(((RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase)context.NativeGeneration).Value,
                    [new(carer.GetUniqueLoadID(), "care-probe", "care-probe", 0, 1, false, [Hold(Find.TickManager.TicksGame + 100)])]);
                var rejected = NativePawnActivities.Validate(probe, context)?.Detail == "Native baby care is still occupied.";
                if (feeding) feedingProtected = rejected;
                if (returning) feedingReturnProtected = rejected;
            }
        }
        feedingFinished |= feedingIntake && feedingReturn && !NativeBabyCare.Occupied(carer) && !carer.IsCarryingPawn(baby);
    }

    private static Fx.BabyFeedingResponseRead FeedingResponseRead() => new()
    {
        Success = feedingBaby != null && feedingCarer != null,
        Baby = feedingBaby?.GetUniqueLoadID() ?? "", Carer = feedingCarer?.GetUniqueLoadID() ?? "",
        CompetingMethod = feedingCompetingMethod, HungerStaged = feedingStaged, Released = feedingReleased,
        FeedingSeen = feedingSeen, Intake = feedingIntake, ReturnSeen = feedingReturn,
        FeedingProtected = feedingProtected, ReturnProtected = feedingReturnProtected,
        Finished = feedingFinished, ReassignedDuringCare = feedingReassigned,
        Food = feedingBaby?.needs.food.CurLevelPercentage ?? 0, Malnutrition = feedingMalnutrition, Dead = feedingBaby?.Dead ?? false,
    };

    [Tool("test/feeding_response_read", Description = "Read observed competing governor work, native feeding, actual food gain and protection through safe return. No mutation.")]
    public async Task<object> ReadFeedingResponse(INativeContext ctx, CancellationToken ct) =>
        await ctx.MainThread.InvokeAsync<object>(() => FixtureKit.Reply(FeedingResponseRead()), ct).ConfigureAwait(false);

    [Tool("test/feeding_response_admit", Description = "UNSAFE FOR MODEL EXECUTION. Admit one deterministic competing harvest Method to the live journal after its enabled review. Normal worker/Hands performs all dispatch and continuation; no feeding or release command.")]
    public async Task<object> AdmitFeedingWork(INativeContext ctx, CancellationToken ct) =>
        await ctx.MainThread.InvokeAsync<object>(() =>
        {
            if (!feedingAdmitted)
            {
                var host = BrainBootstrap.Host ?? throw new InvalidOperationException("The in-process brain is required.");
                var review = host.Journal.LoadReview(ct);
                var world = review.Snapshot;
                var context = Context();
                if (!review.Enabled || !world.IsValid || world.Load.Value != context.Identity.LoadToken
                    || world.Native.Value != ((RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase)context.NativeGeneration).Value)
                    return FixtureKit.Reply(FeedingResponseRead()); // No admission before the real enabled review.
                var carer = feedingCarer ?? throw new InvalidOperationException("Prepare feeding first.");
                var crop = feedingCrop ?? throw new InvalidOperationException("The staged crop is missing.");
                var giver = DefDatabase<WorkGiverDef>.AllDefsListForReading.Single(g => g.Worker is WorkGiver_GrowerHarvest);
                var method = new MethodId("feeding-contention");
                var id = PlanIds.MintPlanId();
                var step = new PawnActivityStep(PawnActivityKind.Work, crop.GetUniqueLoadID(), giver.defName,
                    JobDefOf.Harvest.defName, 0, Find.TickManager.TicksGame + 1000);
                var batch = new PawnActivities(world.Native.Value,
                    [new(carer.GetUniqueLoadID(), "feeding-contention/carer", method.Value, 0, 1, false, [step])]);
                var commit = host.Journal.CommitClaimedMethod(AdminFamily.WorkAssignments, world, method,
                    "native feeding contention precondition", new PlanSpec(id, new(1),
                        [DomainAction.NewPawnActivitiesAction(new(id.Value + "-work"), batch)]), ct);
                if (commit.Outcome != ClaimOutcome.Admitted)
                    throw new InvalidOperationException("Competing work admission: " + commit.Outcome + " " + commit.Conflict?.Reason + " " + commit.Refusal?.Message);
                feedingAdmitted = true;
            }
            return FixtureKit.Reply(FeedingResponseRead());
        }, ct).ConfigureAwait(false);
}
