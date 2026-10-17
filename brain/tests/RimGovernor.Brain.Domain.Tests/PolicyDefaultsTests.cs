using RimGovernor.Brain.Domain;
using System.Buffers;
using System.Text.Json;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Policy reads of facts the in-proc host leaves at their defaults must not throw (#3139, #3140).</summary>
public sealed class PolicyDefaultsTests
{
    [Fact]
    public void ShelterInteriorAreaCountsOnlyTheCapabilitiesWithACellFootprint()
    {
        Assert.True(LayoutShelterRules.ShelterInteriorArea(3, 0, 0) > 0);
    }

    [Fact]
    public void ResourceDemandOfDefaultFactsHasEmptyNeeds()
    {
        var f = new RoundsFacts();
        Assert.NotNull(f.ResourceNeeds);
        var demand = ResourceRules.ResourceDemandOf(f, RoundsRules.DefaultRoundsPolicy(), new RoundsLatches());
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
    public void AKnownAbsentRecordSurvivesTheJournalCodec()
    {
        var biotech = new PawnBiotech { Mechanitor = Fact.Known<PawnMechanitor?>(null) };
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Codecs.Default.Write(writer, biotech);
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        var back = Codecs.Default.Read<PawnBiotech>(doc.RootElement);
        Assert.True(back.Mechanitor.TryGetValue(out var mechanitor));
        Assert.Null(mechanitor);
        Assert.False(back.Mech.TryGetValue(out _));
    }
}
