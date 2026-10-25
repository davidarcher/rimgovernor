using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3876: a pawn's work settings, job target, Biotech block and the catalog's blood loss read whole. The wire names
/// a pawn that never works and one whose work tracker is not initialized; the planner reads each as a pawn with no
/// rows, a job's targetA as a named target, and its own decision and workshop choice as named states.
/// </summary>
public sealed class WorkPlanningStatesTests
{
    private static readonly DefinitionCatalog Catalog = new(
        [
            new Verse.ThingDef { defName = "Silver" },
            new Verse.TerrainDef { defName = "Soil" },
            new Verse.ThingDef { defName = "Human", race = new Verse.RaceProperties { intelligence = Verse.Intelligence.Humanlike, lifeStageAges = [new() { minAge = 0 }] } },
            new Verse.GeneDef { defName = "TestGene" },
            new Verse.MechWorkModeDef { defName = nameof(RimWorld.MechWorkModeDefOf.Work) },
            new Verse.MechWorkModeDef { defName = nameof(RimWorld.MechWorkModeDefOf.Escort) },
            new Verse.MechWorkModeDef { defName = nameof(RimWorld.MechWorkModeDefOf.Recharge) },
            new Verse.HediffDef { defName = "BloodLoss", stages = [new Verse.HediffStage { label = "minor", minSeverity = 0.15f }, new Verse.HediffStage { label = "moderate", minSeverity = 0.5f }] },
        ],
        DefStatTable.Empty, new CatalogDerived("Silver", "Wort", 10f));

    private static WorkPawn Lift(RimGovernor.Brain.GameFacts.Observations.PawnState row) => WorkReads.WorkPawnRow(row, Catalog, Things.Of());

    [Fact]
    public void WorkPawnRetainsTheNativePawnAndItsSkillEvidence()
    {
        var skill = new RimGovernor.Brain.GameFacts.Observations.Skill { DefName = "Construction", Level = 9, StoredLevel = 7, Passion = RimGovernor.Brain.GameFacts.Observations.Passion.Major,
            Learning = SkillStockTests.Mechanics(7, 12) };
        var original = PawnFixtures.Row("builder");
        var row = original with { Biography = original.Biography with { Skills = [skill], BiologicalAgeYears = 41 },
            Health = original.Health with { SummaryFraction = .6, BloodLoss = .2 },
            Needs = original.Needs with { Mood = new RimGovernor.Brain.GameFacts.Observations.NeedLevel.Has(.4), BreakThresholdMinor = .25 } };

        var pawn = Lift(row);

        Assert.Same(row, pawn.Observed);
        Assert.Same(skill, Assert.Single(pawn.Skills));
        Assert.Same(skill.Learning, Assert.Single(pawn.Skills).Learning);
        Assert.Equal(41, pawn.Age);
        Assert.Equal(.6, pawn.Health);
        Assert.Equal(.2, pawn.BloodLoss);
        Assert.Same(row.Needs.Mood, pawn.Mood);
        Assert.Equal(.25, pawn.BreakThreshold);
        var changed = pawn with { Observed = row with { Downed = true, Biography = row.Biography with { Skills = [] } } };
        Assert.False(changed.Available);
        Assert.Empty(changed.Skills);
        Assert.True(pawn.Available);
        Assert.Same(skill, Assert.Single(pawn.Skills));
    }

    private static RimGovernor.Brain.GameFacts.Observations.PawnState WithWork(RimGovernor.Brain.GameFacts.Observations.PawnWork work)
    {
        var row = PawnFixtures.Row("pawn");
        return row with { Settings = row.Settings with { Work = work } };
    }

    [Fact]
    public void EachPawnWorkStateRoundTripsThroughTheWire()
    {
        foreach (var (work, json) in new (RimGovernor.Brain.GameFacts.Observations.PawnWork, string)[]
        {
            (new RimGovernor.Brain.GameFacts.Observations.PawnWork.NeverWorks(), """{"type":"never_works"}"""),
            (new RimGovernor.Brain.GameFacts.Observations.PawnWork.Uninitialized(), """{"type":"uninitialized"}"""),
            (new RimGovernor.Brain.GameFacts.Observations.PawnWork.Works(true, [new RimGovernor.Brain.GameFacts.Observations.WorkSetting { DefName = "Cooking", Priority = 1, Disabled = false }]),
                """{"type":"works","manualPriorities":true,"rows":[{"defName":"Cooking","priority":1,"disabled":false}]}"""),
        })
        {
            var settings = WithWork(work).Settings;
            var text = OrdersJson.Serialize(settings);

            Assert.Contains("\"work\":" + json, text);
            Assert.Equal(text, OrdersJson.Serialize(OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.PawnSettings>(text)));
        }
    }

    [Fact]
    public void APawnThatNeverWorksOrIsUninitializedHasNoRowsToPlan()
    {
        var never = Lift(WithWork(new RimGovernor.Brain.GameFacts.Observations.PawnWork.NeverWorks()));
        Assert.IsType<PawnWorkTable.NeverWorks>(never.Work);
        Assert.False(never.Applies);
        Assert.Empty(never.Work.Priorities());

        var fresh = Lift(WithWork(new RimGovernor.Brain.GameFacts.Observations.PawnWork.Uninitialized()));
        Assert.IsType<PawnWorkTable.Uninitialized>(fresh.Work);
        Assert.True(fresh.Applies);
        Assert.Empty(fresh.Work.Priorities());

        // Another mod's work type has no planner meaning and nothing to write back.
        var modded = Lift(WithWork(new RimGovernor.Brain.GameFacts.Observations.PawnWork.Works(false, [new RimGovernor.Brain.GameFacts.Observations.WorkSetting { DefName = "ModdedWork", Priority = 2, Disabled = false }])));
        var moddedWork = Assert.IsType<PawnWorkTable.Works>(modded.Work);
        Assert.False(moddedWork.Manual);
        Assert.Empty(moddedWork.Rows);
        Assert.Equal("ModdedWork", Assert.Single(moddedWork.Observed.Rows).DefName);

        // Neither is planned; the working pawn beside them still is.
        var worker = PawnFixtures.Worker("worker") with { Work = PawnFixtures.WorkTable(true, [new() { Observed = new RimGovernor.Brain.GameFacts.Observations.WorkSetting { DefName = "Cooking", Priority = 3, Disabled = false }, Skill = "Cooking", Order = 1 }]) };
        var decision = WorkRules.PlanWork([never with { Observed = never.Observed with { Pawn = never.Observed.Pawn with { Id = "never" } } }, fresh with { Observed = fresh.Observed with { Pawn = fresh.Observed.Pawn with { Id = "fresh" } } }, worker], [], new());
        Assert.Equal(WorkDecisionState.Planned, decision.State);
        Assert.Equal([worker.Id], decision.Assignments.Select(a => a.Pawn));
    }

    [Fact]
    public void ACheckboxModeReadbackOrTruncatedClaimsLeaveTheWorkUnplanned()
    {
        var checkbox = PawnFixtures.Worker("pawn") with { Work = PawnFixtures.WorkTable(false, [new() { Observed = new RimGovernor.Brain.GameFacts.Observations.WorkSetting { DefName = "Cooking", Priority = 3, Disabled = false }, Skill = "Cooking", Order = 1 }]) };
        var waiting = WorkRules.PlanWork([checkbox], [], new());
        Assert.Equal(WorkDecisionState.CheckboxMode, waiting.State);
        Assert.False(waiting.Planned);
        Assert.Empty(waiting.Assignments);

        var truncated = WorkRules.PlanWork([checkbox with { Work = PawnFixtures.WorkTable(true, []) }], [], new() { WorkClaimsTruncated = true });
        Assert.Equal(WorkDecisionState.ClaimsTruncated, truncated.State);
        Assert.False(truncated.Planned);
    }

    [Fact]
    public void AJobsTargetANamesTheThingOrCellItWorks()
    {
        static PawnJob Job(RimGovernor.Brain.GameFacts.Observations.JobEvidence job) => Lift(PawnFixtures.Row("pawn") with { Job = job }).Job;
        var thing = new RimGovernor.Brain.GameFacts.Common.Ref { Id = "Thing_Steel1" };

        Assert.Equal(new JobTarget.None(), Job(new RimGovernor.Brain.GameFacts.Observations.JobEvidence()).Target);
        var spawned = Job(new RimGovernor.Brain.GameFacts.Observations.JobEvidence { DefName = "HaulToCell", TargetA = new() { Target = new RimGovernor.Brain.GameFacts.Observations.TargetRefTarget.EntityCase(thing, new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(0, new Cell(3, 4))) } }).Target;
        Assert.Equal(new JobTarget.Spawned("Thing_Steel1", new Cell(3, 4)), spawned);
        Assert.True(spawned.CellOf(out var at));
        Assert.Equal(new Cell(3, 4), at);
        var held = Job(new RimGovernor.Brain.GameFacts.Observations.JobEvidence { DefName = "Ingest", TargetA = new() { Target = new RimGovernor.Brain.GameFacts.Observations.TargetRefTarget.EntityCase(thing, new RimGovernor.Brain.GameFacts.Observations.EntityPlace.Unplaced()) } }).Target;
        Assert.Equal(new JobTarget.Held("Thing_Steel1"), held);
        Assert.Equal("Thing_Steel1", held.Thing);
        Assert.False(held.CellOf(out _));
        var ground = Job(new RimGovernor.Brain.GameFacts.Observations.JobEvidence { DefName = "Goto", TargetA = new() { Target = new RimGovernor.Brain.Domain.Cell { X = 7, Z = 8 } } }).Target;
        Assert.Equal(new JobTarget.Ground(new Cell(7, 8)), ground);
        Assert.Equal("", ground.Thing);
        Assert.Equal(new JobTarget.None(), Job(new RimGovernor.Brain.GameFacts.Observations.JobEvidence { DefName = "Wait", TargetA = new() { Target = new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotLoaded } } }).Target);

        // Native writes targetA on every job.
        Assert.Throws<RequiredFactException>(() => Job(new RimGovernor.Brain.GameFacts.Observations.JobEvidence { DefName = "Wait" }).Target);
        Assert.Throws<RequiredFactException>(() => Job(new RimGovernor.Brain.GameFacts.Observations.JobEvidence { DefName = "Wait", TargetA = new() }).Target);
    }

    [Fact]
    public void TheBiotechBlockIsActiveOrInactiveAndADeathresterIsUnavailable()
    {
        var plain = Lift(PawnFixtures.Row("pawn"));
        Assert.IsType<PawnBiotechStatus.Inactive>(plain.Biotech);
        Assert.True(plain.Available);

        RimGovernor.Brain.GameFacts.Observations.PawnState Deathrest(bool resting) => PawnFixtures.Row("pawn") with
        {
            Biotech = new RimGovernor.Brain.GameFacts.Observations.PawnBiotechState.Active(new RimGovernor.Brain.GameFacts.Observations.PawnBiotech
            {
                DevelopmentalStage = RimGovernor.Brain.GameFacts.Defs.DevelopmentalStage.Adult, Genes = [], Mechanitor = null, Mech = null,
                Deathrest = new RimGovernor.Brain.GameFacts.Observations.PawnDeathrest { Deathresting = resting, Capacity = 1 },
            }),
        };
        var awake = Lift(Deathrest(false));
        Assert.Equal(1, Assert.IsType<PawnBiotechStatus.Active>(awake.Biotech).Facts.Deathrest!.Capacity);
        Assert.True(awake.Available);
        Assert.False(Lift(Deathrest(true)).Available);
    }

    [Fact]
    public void TheSeriousBloodLossIsTheCatalogsModerateStage()
    {
        Assert.Equal(0.5, Lift(PawnFixtures.Row("pawn")).SeriousBloodLoss);
        var bare = new DefinitionCatalog([new Verse.ThingDef { defName = "Silver" }, new Verse.TerrainDef { defName = "Soil" }], DefStatTable.Empty, new CatalogDerived("Silver", "Wort", 10f));
        Assert.Throws<CatalogException>(() => bare.SeriousBloodLoss());
        Assert.Throws<CatalogException>(() => WorkReads.WorkPawnRow(PawnFixtures.Row("pawn"), bare, Things.Of()));
    }

    [Fact]
    public void ACarePolicyMapsToTheDomainsModesAndAnUnspecifiedOneThrows()
    {
        Assert.Equal(MedicalCare.Herbal, WorkRules.MedicalCareOf(RimGovernor.Brain.GameFacts.Operations.MedicalCare.HerbalOrWorse));
        Assert.Equal(HostilityResponse.Flee, WorkRules.HostilityOf(RimGovernor.Brain.GameFacts.Operations.HostilityResponse.Flee));
        Assert.Throws<DomainException>(() => WorkRules.MedicalCareOf((RimGovernor.Brain.GameFacts.Operations.MedicalCare)0));
        Assert.Throws<DomainException>(() => WorkRules.HostilityOf((RimGovernor.Brain.GameFacts.Operations.HostilityResponse)0));

        // A pawn without care settings has no hostility row: it has no response to change.
        var pawn = PawnFixtures.Worker("pawn");
        Assert.Null(pawn.HostilityPawn());
        var flees = pawn with { Observed = pawn.Observed with { Settings = pawn.Observed.Settings with { Care = new RimGovernor.Brain.GameFacts.Observations.PawnCare.Configured(RimGovernor.Brain.GameFacts.Operations.MedicalCare.Best, false, RimGovernor.Brain.GameFacts.Operations.HostilityResponse.Flee) } } };
        Assert.Equal(HostilityResponse.Flee, flees.HostilityPawn()!.Current);
        Assert.Same(flees, flees.HostilityPawn()!.Pawn);
        Assert.Same(flees.Observed.Job, flees.HostilityPawn()!.Job.Observed);
        Assert.Equal(HostilityResponse.Attack, HostilityRules.DefaultHostility(flees.HostilityPawn()!));
        var injured = flees with { Observed = flees.Observed with { Health = flees.Observed.Health with { SummaryFraction = .3 } } };
        Assert.Equal(HostilityResponse.Flee, HostilityRules.DefaultHostility(injured.HostilityPawn()!));
    }

    [Fact]
    public void AWorkshopWhoseBenchDefinitionIsUnreadNamesIt()
    {
        var request = new WorkshopRequest
        {
            Resource = new("Cloth"),
            Benches = [],
            Hosts = [new RecipeHost { Definition = "MakeCloth", Products = [new("Cloth")], Available = true, Benches = ["TableTailor"], Ingredients = [], Research = [] }],
        };
        Assert.Throws<RequiredFactException>(() => WorkRules.SelectWorkshopBench(request));

        var census = request with { Definitions = [new BenchDefinition { Name = "TableTailor", Available = true, NeedsPower = false, ConstructionSkill = 0}] };
        Assert.Equal(WorkshopMethod.Build, WorkRules.SelectWorkshopBench(census).Method);
    }
}
