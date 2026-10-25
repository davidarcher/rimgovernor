using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Bridge;
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
        var error = Assert.Throws<BridgeContractException>(() => ColonyPlanningReads.Compose(ColonyFactsCoreTests.Snapshot(), new ObservationIdentity { Context = ColonyFactsCoreTests.Snapshot().Context }, tables, null));
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
        var human = new Verse.ThingDef { defName = "Human", race = new Verse.RaceProperties { intelligence = Verse.Intelligence.Humanlike, lifeStageAges = [new() { minAge = 0 }] } };
        var stats = new RimGovernor.Brain.Bridge.DefStatTable(
            new Dictionary<(Verse.BuildableDef, Verse.ThingDef?), RimGovernor.Brain.Bridge.DefStatTable.Cost> { [(silver, null)] = new([], null), [(human, null)] = new([], null) },
            new Dictionary<(Verse.BuildableDef, Verse.ThingDef?, RimGovernor.Brain.Bridge.CatalogStat), RimGovernor.Brain.Bridge.DefStatTable.Value>(),
            new Dictionary<Verse.BuildableDef, IReadOnlyList<string>>(),
            new HashSet<RimGovernor.Brain.Bridge.CatalogStat>(RimGovernor.Brain.Bridge.CatalogStats.All));
        var catalog = new RimGovernor.Brain.Bridge.DefinitionCatalog([
            silver, new Verse.TerrainDef { defName = "Soil" },
            human,
            new Verse.GeneDef { defName = "Robust" },
            new Verse.MechWorkModeDef { defName = nameof(RimWorld.MechWorkModeDefOf.Work) },
            new Verse.MechWorkModeDef { defName = nameof(RimWorld.MechWorkModeDefOf.Escort) },
            new Verse.MechWorkModeDef { defName = nameof(RimWorld.MechWorkModeDefOf.Recharge) },
            new Verse.HediffDef { defName = "BloodLoss", stages = [new Verse.HediffStage { label = "moderate", minSeverity = .5f }] }],
            stats, new RimGovernor.Brain.Bridge.CatalogDerived("Silver", "Wort", 10f));
        var row = EmergencyFixtures.Placed("pawn");
        row = row with
        {
            Health = row.Health with { NeedsTend = needsTend },
            Biotech = new O.PawnBiotechState.Active(new O.PawnBiotech
            {
                DevelopmentalStage = RimGovernor.Contracts.Wire.Defs.DevelopmentalStage.Adult,
                Genes = [], Mech = null, Deathrest = null,
                Mechanitor = new() { UsedBandwidth = 0, TotalBandwidth = 6, UsedBandwidthFromGestation = 0, ControlGroups = 1, ControlledMechs = [] },
            }),
        };
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
            Zones = new() { Context = status.Context!, Zones = [] },
            Ideology = new O.IdeologyFrame.NotInstalled(),
        };
        var routine = FacadeNativeClient.DecodeRoundsFrame(frame, catalog, FramePrepared.Of(frame));
        var tables = routine.Tables;
        Assert.Same(table, tables.PawnCensus);
        Assert.Same(frame.Zones, tables.Zones);
        Assert.Equal("pawn", Assert.Single(routine.Pawns!.Pawns).Pawn.Id);
        var projection = ColonyPawnReads.WithPawnCensus(ColonyFactsCoreTests.Reading(), ColonyFactsCoreTests.Snapshot() with { ColonistCount = 1 }, tables, catalog);
        Assert.Equal("pawn", Assert.Single(projection.Facts.MedicalPawns).Id.Value);
        Assert.Equal("pawn", Assert.Single(projection.Facts.MoodPawns).Id.Value);
        Assert.Same(row, Assert.Single(projection.Facts.MoodPawns).Pawn);
        Assert.Same(row, Assert.Single(projection.Facts.MedicalPawns).Observed);
        Assert.Equal("pawn", Assert.Single(projection.Facts.ForecastObserved.WorkPawns.Pawns).Id.Value);
        Assert.Equal("pawn", Assert.Single(projection.Facts.Mechs.Mechanitors).Id.Value);
        Assert.Empty(projection.Facts.Mechs.Mechs);
        Assert.Equal("pawn", Assert.Single(projection.Facts.WorkProfiles).Id.Value);
        Assert.True(projection.Facts.MedicalCareRecovered);
        Assert.Equal(critical, projection.Facts.CriticalPatients);
        Assert.Equal(0, projection.Facts.UrgentPatients);
        Assert.Equal(0, projection.Facts.Armed);
        Assert.Equal(1, projection.Facts.Unarmed);
    }
    [Fact]
    public void StationMoodNamesTheAbsenceOfMoodBearingColonists()
    {
        Assert.IsType<StationMood.NoMood>(StationMood.Of([]));
        Assert.IsType<StationMood.NoMood>(StationMood.Of([MoodFixtures.Pawn("none")]));
        var result = Assert.IsType<StationMood.Expected>(StationMood.Of([
            MoodFixtures.Has("a", .2, .4),
            MoodFixtures.Has("b", .8, .6)]));
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
        var dead = MedicalRules.ReviewMedicalCare([patient with { Observed = patient.Observed with { Dead = true } }], absent);
        Assert.False(dead.Recovered());
        Assert.Equal([patient.Id], dead.Unresolved);
        var recovered = MedicalRules.ReviewMedicalCare([patient], dead);
        Assert.True(recovered.Recovered());
        Assert.Empty(recovered.Unresolved);
    }
}
