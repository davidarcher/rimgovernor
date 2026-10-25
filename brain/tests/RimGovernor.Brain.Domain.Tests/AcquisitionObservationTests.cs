using RimGovernor.Brain.Policy;
using O = RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class AcquisitionObservationTests
{
    [Fact]
    public void SourceKeepsTheNativeRowAndEntityThroughSelectionAndAHold()
    {
        var observed = new O.AcquisitionFacts
        {
            Source = new() { Id = "deer" }, Token = "deer-cas", Resource = "Deer", Yield = 1,
            Designation = new O.AcquisitionDesignation.Open(), Taken = false,
            Kind = new O.AcquisitionKind.Hunt(82, 3, false, true, false, false),
        };
        var entity = new O.EntityRef
        {
            Id = "deer", DefName = "Deer", Label = "deer",
            Place = new O.EntityPlace.OnMap(1, new Cell(10, 10)),
        };
        var source = new AcquisitionSource { Observed = observed, Entity = entity, NutritionYield = 4.1 };
        var selected = Assert.Single(AcquisitionRules.SelectAcquisition([source], 4, 0, true, new Dictionary<string, bool>(), 1));
        Assert.Same(source, selected);
        Assert.Same(observed, selected.Observed);
        Assert.Same(entity, selected.Entity);
        Assert.Equal(("deer", "deer-cas", "Deer", new Cell(10, 10)), (selected.Id, selected.Token, selected.Definition, selected.Cell));
        Assert.Equal(3u, selected.HerdSize);
        Assert.True(selected.Sleeping);
        Assert.True(selected.Food);
        var hold = Assert.IsType<HuntHold>(new HuntCensus().Gate(new HuntPrey { Source = source }).Hold);
        Assert.Equal(HuntGateRules.HuntHoldNoColonist, hold.Reason);
        Assert.Same(source, hold.Source);
    }

    [Fact]
    public void HuntGateReadsPreyAndHunterFlagsFromTheirNativeRows()
    {
        var observed = new O.HunterFacts
        {
            Pawn = new("hunter"), Position = new(0, 0), Downed = false, InMentalState = false,
            HuntingActive = true, WeaponDef = "Gun_BoltActionRifle", RangedBlockingShield = false,
        };
        var hunter = new HuntHunter
        {
            Observed = observed,
            Weapon = new HuntWeapon { Ranged = true, Verbs = [new HuntVerb { AIWeapon = true, Range = 30, Projectile = HuntProjectile.Bullet, DamageDef = "Bullet" }] },
        };
        var source = new AcquisitionSource
        {
            Observed = new()
            {
                Source = new() { Id = "deer" }, Token = "deer-cas", Resource = "Deer", Yield = 1,
                Designation = new O.AcquisitionDesignation.Open(), Taken = false,
                Kind = new O.AcquisitionKind.Hunt(82, 1, false, false, false, false),
            },
            Entity = new() { Id = "deer", DefName = "Deer", Label = "deer", Place = new O.EntityPlace.OnMap(1, new Cell(10, 10)) },
        };
        var prey = new HuntPrey { Source = source };
        var census = new HuntCensus { Hunters = [hunter] };
        Assert.Same(observed, hunter.Observed);
        Assert.Equal(("hunter", new Cell(0, 0)), (hunter.ID, hunter.Cell));
        Assert.Null(census.Gate(prey).Hold);
        Assert.Equal(30, census.Gate(prey).WeaponRange);
        var downed = census with { Hunters = [hunter with { Observed = observed with { Downed = true } }] };
        Assert.Equal(["hunter downed"], downed.Gate(prey).Hold!.Detail);
        var kind = Assert.IsType<O.AcquisitionKind.Hunt>(source.Observed.Kind);
        var fogged = new HuntPrey { Source = source with { Observed = source.Observed with { Kind = kind with { Fogged = true } } } };
        Assert.Equal(HuntGateRules.HuntHoldFogged, census.Gate(fogged).Hold!.Reason);
        var mental = new HuntPrey { Source = source with { Observed = source.Observed with { Kind = kind with { InMentalState = true } } } };
        Assert.Equal(HuntGateRules.HuntHoldNotSafePrey, census.Gate(mental).Hold!.Reason);
    }
}
