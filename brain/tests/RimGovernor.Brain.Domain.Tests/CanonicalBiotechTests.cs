using System.Text.Json;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using O = RimGovernor.Contracts.Wire.Observations;
using D = RimGovernor.Contracts.Wire.Defs;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CanonicalBiotechTests
{
    [Fact]
    public void BiotechCatalogViewKeepsWholeNativeRecordsThroughTheJournal()
    {
        var observed = new O.PawnBiotech
        {
            DevelopmentalStage = D.DevelopmentalStage.Adult,
            Genes = [new() { DefName = "gene", Active = true }],
            Mechanitor = new() { UsedBandwidth = 1, TotalBandwidth = 5, UsedBandwidthFromGestation = 2,
                ControlGroups = 1, ControlledMechs = [new() { Id = "mech" }] },
            Mech = new() { Overseer = new() { Id = "overseer" }, WorkMode = "Work",
                Energy = new O.PawnMechEnergy.Level { Fraction = 0.6 },
                Group = new() { Index = 1, RechargeBelow = 0.2, RechargeAbove = 0.9 } },
            Deathrest = new() { Deathresting = true, Capacity = 3 },
        };
        var view = new PawnBiotech { Observed = observed };
        Assert.Same(observed.Mechanitor, view.Mechanitor);
        Assert.Same(observed.Mech, view.Mech);
        var json = StoreJson.Serialize(view);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(["DevelopmentalStage", "Genes", "Effects", "WorkMinAges", "Mechanitor", "Mech", "Deathrest"],
            document.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(observed, StoreJson.Deserialize<PawnBiotech>(json).Observed);
    }
}