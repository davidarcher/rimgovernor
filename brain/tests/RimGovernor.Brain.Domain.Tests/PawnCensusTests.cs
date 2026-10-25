using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class PawnCensusTests
{
    [Fact]
    public void DirectColonyDecoderRefusesAnAbsentPawnCensus()
    {
        var tables = new Tables(default, default, default, []);
        var error = Assert.Throws<BridgeContractException>(() => ColonyDecodeReads.DecodeColony(ColonyFactsCoreTests.Snapshot(), new Identity(), tables, null));
        Assert.Contains("pawn census", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RoutineFrameDecoderRefusesAnAbsentPawnCensus()
    {
        var frame = new O.BundleSnapshot { Context = ColonyFactsCoreTests.Snapshot().Context };
        var error = Assert.Throws<BridgeContractException>(() => FacadeNativeClient.DecodeRoundsFrame(frame, null!, FramePrepared.Of(frame)));
        Assert.Contains("pawn census", error.Message, StringComparison.Ordinal);
    }
    [Fact]
    public void EmergencyDecoderRefusesAnUnresolvedColonist()
    {
        Assert.Throws<BridgeContractException>(() => FacadeNativeClient.EmergencyDecode.DecodeEmergencyStatus(EmergencyFixtures.Status("missing"), Pawns.Of()));
    }

    [Fact]
    public void EmptyColonistCensusIsACompletePawnSnapshot()
    {
        var status = EmergencyFixtures.Status();
        var emergency = FacadeNativeClient.EmergencyDecode.DecodeEmergencyStatus(status, Pawns.Of());
        var snapshot = new O.PawnSnapshot { Context = status.Context, Pawns = [] };
        Assert.Empty(FacadeNativeClient.RoundsPawnRows(snapshot, Pawns.Of(), emergency).Pawns);
    }

    [Fact]
    public void AContradictoryMedicalRosterFailsRatherThanAppearingRecovered()
    {
        var source = ColonyFactsCoreTests.Snapshot() with { ColonistCount = 1 };
        var pawns = new O.PawnSnapshot { Context = source.Context, Pawns = [] };
        Assert.Throws<ObservationContractException>(() => RoundsContainmentReads.RoundsMedical(source, new EmergencyFacts(), pawns, null!));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void CommonColonyDecoderBuildsMedicalMoodWorkAndArmedSectionsFromTheActualTable(bool needsTend, long critical)
    {
        var silver = new Verse.ThingDef { defName = "Silver" };
        var stats = new RimGovernor.Brain.Bridge.DefStatTable(
            new Dictionary<(Verse.BuildableDef, Verse.ThingDef?), RimGovernor.Brain.Bridge.DefStatTable.Cost> { [(silver, null)] = new([], null) },
            new Dictionary<(Verse.BuildableDef, Verse.ThingDef?, RimGovernor.Brain.Bridge.CatalogStat), RimGovernor.Brain.Bridge.DefStatTable.Value>(),
            new Dictionary<Verse.BuildableDef, IReadOnlyList<string>>(),
            new HashSet<RimGovernor.Brain.Bridge.CatalogStat>(RimGovernor.Brain.Bridge.CatalogStats.All));
        var catalog = new RimGovernor.Brain.Bridge.DefinitionCatalog([
            silver, new Verse.TerrainDef { defName = "Soil" },
            new Verse.HediffDef { defName = "BloodLoss", stages = [new Verse.HediffStage { label = "moderate", minSeverity = .5f }] }],
            stats, new RimGovernor.Brain.Bridge.CatalogDerived("Silver", "Wort", 10f));
        var row = EmergencyFixtures.Placed("pawn");
        row = row with { Health = row.Health with { NeedsTend = needsTend } };
        row = row with { Settings = row.Settings with { Care = new O.PawnCare.Configured(RimGovernor.Contracts.Wire.Operations.MedicalCare.HerbalOrWorse, false, RimGovernor.Contracts.Wire.Operations.HostilityResponse.Flee) } };
        var status = EmergencyFixtures.Status("pawn");
        var table = new O.PawnSnapshot { Context = status.Context, Pawns = [row] };
        var frame = new O.BundleSnapshot
        {
            Context = status.Context,
            Pawns = table,
            Emergency = status,
            Research = new() { Context = status.Context!, Slots = [], Projects = [], Finished = [] },
            Rooms = new() { Context = status.Context!, Rooms = [] },
            Ideology = new O.IdeologyFrame.NotInstalled(),
        };
        var routine = FacadeNativeClient.DecodeRoundsFrame(frame, catalog, FramePrepared.Of(frame));
        var tables = routine.Tables;
        Assert.Same(table, tables.PawnCensus);
        Assert.Equal("pawn", Assert.Single(routine.Pawns!.Pawns).Pawn.Id);
        var projection = ColonyPawnReads.WithPawnCensus(ColonyFactsCoreTests.Projection(), ColonyFactsCoreTests.Snapshot() with { ColonistCount = 1 }, tables, catalog);
        Assert.Equal("pawn", Assert.Single(projection.Facts.MedicalPawns).Id.Value);
        Assert.Equal("pawn", Assert.Single(projection.Facts.MoodPawns).Id.Value);
        Assert.Equal("pawn", Assert.Single(projection.WorkPawns.Pawns).Id.Value);
        Assert.Equal("pawn", Assert.Single(projection.Facts.WorkProfiles).Id.Value);
        Assert.True(projection.Facts.MedicalCareRecovered);
        Assert.Equal(critical, projection.Facts.CriticalPatients);
        Assert.Equal(0, projection.Facts.UrgentPatients);
        Assert.Equal(0, projection.Facts.Armed);
        Assert.Equal(1, projection.Facts.Unarmed);
        Assert.Equal(projection.WorkPawns, projection.Facts.ForecastObserved.WorkPawns);
    }
    [Fact]
    public void StationMoodNamesTheAbsenceOfMoodBearingColonists()
    {
        Assert.IsType<StationMood.NoMood>(StationMood.Of([]));
        Assert.IsType<StationMood.NoMood>(StationMood.Of([new MoodPawn { Id = new("none"), Mood = new MoodLevel.Lacks() }]));
        var result = Assert.IsType<StationMood.Expected>(StationMood.Of([
            new MoodPawn { Id = new("a"), Mood = new MoodLevel.Has(.2, .4) },
            new MoodPawn { Id = new("b"), Mood = new MoodLevel.Has(.8, .6) }]));
        Assert.Equal(.5, result.Level, 10);
    }
    [Fact]
    public void OnlyObservedRecoveryClearsTrackedMedicalCare()
    {
        var patient = CareFixtures.Patient("patient");
        var history = new MedicalCareHistory { Patients = [patient.Id] };
        var absent = MedicalRules.ReviewMedicalCare([], history);
        Assert.False(absent.Recovered());
        Assert.Equal([patient.Id], absent.Unresolved);
        var dead = MedicalRules.ReviewMedicalCare([patient with { Dead = true }], absent);
        Assert.False(dead.Recovered());
        Assert.Equal([patient.Id], dead.Unresolved);
        var recovered = MedicalRules.ReviewMedicalCare([patient], dead);
        Assert.True(recovered.Recovered());
        Assert.Empty(recovered.Unresolved);
    }
}