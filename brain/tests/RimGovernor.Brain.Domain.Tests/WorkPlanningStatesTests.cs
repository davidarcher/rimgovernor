using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;
using Ops = RimGovernor.Contracts.Wire.Operations;

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
            new Verse.HediffDef { defName = "BloodLoss", stages = [new Verse.HediffStage { label = "minor", minSeverity = 0.15f }, new Verse.HediffStage { label = "moderate", minSeverity = 0.5f }] },
        ],
        DefStatTable.Empty, new CatalogDerived("Silver", "Wort", 10f));

    private static WorkPawn Lift(O.PawnState row) => RoundsWorkReads.WorkPawnRow(row, Catalog, Things.Of());

    private static O.PawnState WithWork(O.PawnWork work)
    {
        var row = PawnFixtures.Row("pawn");
        return row with { Settings = row.Settings with { Work = work } };
    }

    [Fact]
    public void EachPawnWorkStateRoundTripsThroughTheWire()
    {
        foreach (var (work, json) in new (O.PawnWork, string)[]
        {
            (new O.PawnWork.NeverWorks(), """{"type":"never_works"}"""),
            (new O.PawnWork.Uninitialized(), """{"type":"uninitialized"}"""),
            (new O.PawnWork.Works(true, [new O.WorkSetting { DefName = "Cooking", Priority = 1, Disabled = false }]),
                """{"type":"works","manualPriorities":true,"rows":[{"defName":"Cooking","priority":1,"disabled":false}]}"""),
        })
        {
            var settings = WithWork(work).Settings;
            var text = WireJson.Serialize(settings);

            Assert.Contains("\"work\":" + json, text);
            Assert.Equal(text, WireJson.Serialize(WireJson.Deserialize<O.PawnSettings>(text)));
        }
    }

    [Fact]
    public void APawnThatNeverWorksOrIsUninitializedHasNoRowsToPlan()
    {
        var never = Lift(WithWork(new O.PawnWork.NeverWorks()));
        Assert.IsType<PawnWorkTable.NeverWorks>(never.Work);
        Assert.False(never.Applies);
        Assert.Empty(never.Work.Priorities());

        var fresh = Lift(WithWork(new O.PawnWork.Uninitialized()));
        Assert.IsType<PawnWorkTable.Uninitialized>(fresh.Work);
        Assert.True(fresh.Applies);
        Assert.Empty(fresh.Work.Priorities());

        // Another mod's work type has no planner meaning and nothing to write back.
        var modded = Lift(WithWork(new O.PawnWork.Works(false, [new O.WorkSetting { DefName = "ModdedWork", Priority = 2, Disabled = false }])));
        Assert.Equal(new PawnWorkTable.Works(false, []), modded.Work);

        // Neither is planned; the working pawn beside them still is.
        var worker = PawnFixtures.Worker("worker") with { Work = new PawnWorkTable.Works(true, [new() { Work = WorkType.Cooking, Skill = "Cooking", Order = 1, Priority = 3 }]) };
        var decision = WorkRules.PlanWork([never with { Id = new("never") }, fresh with { Id = new("fresh") }, worker], [], new());
        Assert.Equal(WorkDecisionState.Planned, decision.State);
        Assert.Equal([worker.Id], decision.Assignments.Select(a => a.Pawn));
    }

    [Fact]
    public void ACheckboxModeReadbackOrTruncatedClaimsLeaveTheWorkUnplanned()
    {
        var checkbox = PawnFixtures.Worker("pawn") with { Work = new PawnWorkTable.Works(false, [new() { Work = WorkType.Cooking, Skill = "Cooking", Order = 1, Priority = 3 }]) };
        var waiting = WorkRules.PlanWork([checkbox], [], new());
        Assert.Equal(WorkDecisionState.CheckboxMode, waiting.State);
        Assert.False(waiting.Planned);
        Assert.Empty(waiting.Assignments);

        var truncated = WorkRules.PlanWork([checkbox with { Work = new PawnWorkTable.Works(true, []) }], [], new() { WorkClaimsTruncated = true });
        Assert.Equal(WorkDecisionState.ClaimsTruncated, truncated.State);
        Assert.False(truncated.Planned);
    }

    [Fact]
    public void AJobsTargetANamesTheThingOrCellItWorks()
    {
        static PawnJob Job(O.JobEvidence job) => Lift(PawnFixtures.Row("pawn") with { Job = job }).Job;
        var thing = new C.Ref { Id = "Thing_Steel1" };

        Assert.Equal(new JobTarget.None(), Job(new O.JobEvidence()).Target);
        var spawned = Job(new O.JobEvidence { DefName = "HaulToCell", TargetA = new() { Target = thing }, TargetACell = new C.Cell { X = 3, Z = 4 } }).Target;
        Assert.Equal(new JobTarget.Spawned("Thing_Steel1", new Cell(3, 4)), spawned);
        Assert.True(spawned.CellOf(out var at));
        Assert.Equal(new Cell(3, 4), at);
        var held = Job(new O.JobEvidence { DefName = "Ingest", TargetA = new() { Target = thing } }).Target;
        Assert.Equal(new JobTarget.Held("Thing_Steel1"), held);
        Assert.Equal("Thing_Steel1", held.Thing);
        Assert.False(held.CellOf(out _));
        var ground = Job(new O.JobEvidence { DefName = "Goto", TargetA = new() { Target = new C.Cell { X = 7, Z = 8 } } }).Target;
        Assert.Equal(new JobTarget.Ground(new Cell(7, 8)), ground);
        Assert.Equal("", ground.Thing);
        Assert.Equal(new JobTarget.None(), Job(new O.JobEvidence { DefName = "Wait", TargetA = new() { Target = new C.Unavailable { Reason = C.UnavailableReason.NotLoaded } } }).Target);

        // Native writes targetA on every job.
        Assert.Throws<DomainException>(() => Job(new O.JobEvidence { DefName = "Wait" }));
        Assert.Throws<DomainException>(() => Job(new O.JobEvidence { DefName = "Wait", TargetA = new() }));
    }

    [Fact]
    public void TheBiotechBlockIsActiveOrInactiveAndADeathresterIsUnavailable()
    {
        var plain = Lift(PawnFixtures.Row("pawn"));
        Assert.IsType<PawnBiotechStatus.Inactive>(plain.Biotech);
        Assert.True(plain.Available);

        O.PawnState Deathrest(bool resting) => PawnFixtures.Row("pawn") with
        {
            Biotech = new O.PawnBiotechState.Active(new O.PawnBiotech
            {
                DevelopmentalStage = RimGovernor.Contracts.Wire.Defs.DevelopmentalStage.Adult, Genes = [], Mechanitor = null, Mech = null,
                Deathrest = new O.PawnDeathrest { Deathresting = resting, Capacity = 1 },
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
        Assert.Throws<CatalogException>(() => RoundsWorkReads.WorkPawnRow(PawnFixtures.Row("pawn"), bare, Things.Of()));
    }

    [Fact]
    public void ACarePolicyMapsToTheDomainsModesAndAnUnspecifiedOneThrows()
    {
        Assert.Equal(MedicalCare.Herbal, WorkRules.MedicalCareOf(Ops.MedicalCare.HerbalOrWorse));
        Assert.Equal(HostilityResponse.Flee, WorkRules.HostilityOf(Ops.HostilityResponse.Flee));
        Assert.Throws<DomainException>(() => WorkRules.MedicalCareOf(Ops.MedicalCare.Unspecified));
        Assert.Throws<DomainException>(() => WorkRules.HostilityOf(Ops.HostilityResponse.Unspecified));

        // A pawn without care settings has no hostility row: it has no response to change.
        var pawn = PawnFixtures.Worker("pawn");
        Assert.Null(pawn.HostilityPawn());
        var flees = pawn with { Care = new O.PawnCare.Configured(Ops.MedicalCare.Best, false, Ops.HostilityResponse.Flee) };
        Assert.Equal(HostilityResponse.Flee, flees.HostilityPawn()!.Current);
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
        Assert.Equal(WorkshopMethod.DefinitionUnread, WorkRules.SelectWorkshopBench(request).Method);
        Assert.Equal("definition_unread", Wire.Name(WorkshopMethod.DefinitionUnread));

        var census = request with { Definitions = [new BenchDefinition { Name = "TableTailor", Available = Known(true), NeedsPower = Known(false), ConstructionSkill = Known(0) }] };
        Assert.Equal(WorkshopMethod.Build, WorkRules.SelectWorkshopBench(census).Method);
    }
}
