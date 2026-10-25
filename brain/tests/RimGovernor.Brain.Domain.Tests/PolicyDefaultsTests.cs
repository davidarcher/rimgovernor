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
        var f = new RoundsFacts();
        Assert.NotNull(f.ResourceNeeds);
        Assert.Throws<RequiredFactException>(() => ResourceRules.ResourceDemandOf(f, RoundsRules.DefaultRoundsPolicy(), new RoundsLatches()));
        var demand = ResourceRules.ResourceDemandOf(f with { CurrentConstruction = CurrentConstruction.Empty }, RoundsRules.DefaultRoundsPolicy(), new RoundsLatches());
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
        var mech = new PawnMech { Overseer = "", WorkMode = null, Energy = new MechEnergy.NoEnergyNeed(), Group = null };
        var biotech = new PawnBiotech { DevelopmentalStage = default, Genes = [], Mechanitor = null, Mech = mech, Deathrest = null };
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Codecs.Default.Write(writer, biotech);
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        var back = Codecs.Default.Read<PawnBiotech>(doc.RootElement);
        Assert.Equal(biotech with { Genes = back.Genes }, back);
        Assert.Null(back.Mechanitor);
        Assert.Equal(mech, back.Mech);
    }
}
