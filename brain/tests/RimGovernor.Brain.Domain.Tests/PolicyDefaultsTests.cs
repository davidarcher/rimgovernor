using RimGovernor.Brain.Domain;
using System.Buffers;
using System.Text.Json;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Policy defaults and refusal of missing required census inputs.</summary>
public sealed class PolicyDefaultsTests
{
    [Fact]
    public void ResourceDemandRequiresConstructionCensus()
    {
        var f = new ReviewFacts { Census = ColonyFactsCoreTests.Snapshot(), Gear = ColonyModelFixtures.NoGear() };
        Assert.NotNull(f.ResourceNeeds);
        Assert.Throws<RequiredFactException>(() => ResourceRules.ResourceDemandOf(f, Rules.DefaultPlannerSettings(), new Latches()));
        var demand = ResourceRules.ResourceDemandOf(f with { CurrentConstruction = CurrentConstruction.Empty }, Rules.DefaultPlannerSettings(), new Latches());
        Assert.NotNull(demand.Needs);
    }

    [Fact]
    public void DenseCoreColumnDoesNotRecurseOnItsHalfHeight()
    {
        var cells = new List<Cell>();
        for (var x = 0; x < 3; x++)
        {
            for (var z = 0; z < 25; z++)
            {
                cells.Add(new Cell(x, z));
            }
        }
        Assert.True(new DenseCore(cells).Column(1, 12));
    }

    [Fact]
    public void AnAbsentRecordSurvivesTheJournalCodec()
    {
        var mech = new RimGovernor.Brain.GameFacts.Observations.PawnMech { Overseer = null, WorkMode = null, Energy = new RimGovernor.Brain.GameFacts.Observations.PawnMechEnergy.NoEnergyNeed(), Group = null };
        var biotech = new PawnBiotech { Observed = new RimGovernor.Brain.GameFacts.Observations.PawnBiotech { DevelopmentalStage = default, Genes = [], Mechanitor = null, Mech = mech, Deathrest = null } };
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Codecs.Default.Write(writer, biotech);
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        var back = Codecs.Default.Read<PawnBiotech>(doc.RootElement);
        Assert.Equal(biotech with { Effects = back.Effects, WorkMinAges = back.WorkMinAges }, back);
        Assert.Empty(back.Effects.Stats);
        Assert.Empty(back.Effects.DisabledNeeds);
        Assert.Empty(back.Effects.EnabledNeeds);
        Assert.Empty(back.WorkMinAges);
        Assert.Null(back.Mechanitor);
        Assert.Equal(mech, back.Mech);
    }
}
