using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Mirror;
using Xunit;
using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class NumericBaselineTests
{
    private static CombatPawn Pawn(string id, Cell cell) => new()
    {
        Id = new(id), Cell = cell, Side = CombatSide.Colonist, Downed = false, Dead = false,
        Stance = RimGovernor.Brain.GameFacts.Mirror.CombatStance.Warmup, Health = 1, MoveSpeed = 4, Armor = 0,
        Changed = new Watermark { Tick = 0, Seq = 0 }, MedicalSkill = 0,
        GoJuiceHigh = false, LuciferiumAddicted = false, StunTicksLeft = 0, TargetMortar = false,
    };

    [Fact]
    public void StorageRejectsOverflowButAcceptsALargeFiniteAggregate()
    {
        FoodStock Stock(string id, double nutrition) => new()
        {
            Id = id, DefName = "food", Count = 1, Rot = new FoodRot.Rots(100), RotRate = 1,
            TemperatureC = 20, IsHumanMeat = false, IsHumanlike = false, RawMeat = true, Vegetable = false,
            RawClass = FoodIngredientClass.Meat, Holder = new(""), Eaters = [], Nutrition = nutrition, UnitMass = 0,
            Place = new FoodPlace.Stands(false, ""), Form = new FoodForm.Item(), Forbidden = false,
        };
        FoodStorageObservation Observation(double nutrition) => new()
        {
            Stocks = EquatableArray.From(new[] {
                new FoodStorageStock(Stock("a", nutrition)), new FoodStorageStock(Stock("b", nutrition)) }),
            ChilledMaxC = 0,
        };
        var policy = FoodStorageUpkeepRules.DefaultFoodStoragePolicy();
        var finite = FoodStorageUpkeepRules.ReviewFoodStorage(Observation(1e300), policy);
        Assert.True(double.IsFinite(finite.Target));
        Assert.Throws<DomainException>(() => FoodStorageUpkeepRules.ReviewFoodStorage(Observation(1e308), policy));
    }
    [Fact]
    public void PostFightNeedsAStandingColonistAndBreaksDistanceTiesById()
    {
        var raider = Pawn("raider", new(0, 0)) with { Side = CombatSide.Hostile };
        var a = Pawn("a", new(1, 0));
        var b = Pawn("b", new(0, 1));
        Assert.Null(CombatThreatRules.PostFightColonist([raider, a with { Dead = true }, b with { Downed = true }], raider));
        Assert.Same(a, CombatThreatRules.PostFightColonist([b, a], raider));
        Assert.Same(a, CombatThreatRules.PostFightColonist([a, b], raider));
        var targeting = b with { Cell = new(100, 100), TargetId = raider.Id.Value };
        Assert.Same(targeting, CombatThreatRules.PostFightColonist([a, targeting], raider));
        var targetingA = a with { TargetId = raider.Id.Value };
        Assert.Same(targetingA, CombatThreatRules.PostFightColonist([targeting, targetingA], raider));
    }

    [Fact]
    public void PostFightDistanceDoesNotOverflowAcrossTheCellCoordinateRange()
    {
        var raider = Pawn("raider", new(int.MinValue, int.MinValue));
        var far = Pawn("a", new(int.MaxValue, int.MaxValue));
        var near = Pawn("z", new(int.MinValue + 1, int.MinValue));
        Assert.Same(near, CombatThreatRules.PostFightColonist([far, near], raider));
    }

    [Fact]
    public void AcquisitionCostsHaveNoArbitraryFiniteCeiling()
    {
        Assert.True(ResourceRules.FiniteAcquisitionCost(2e12));
        Assert.False(ResourceRules.FiniteAcquisitionCost(double.PositiveInfinity));
        Assert.False(ResourceRules.FiniteAcquisitionCost(double.NaN));
        Assert.False(ResourceRules.FiniteAcquisitionCost(-1));
    }

    [Fact]
    public void GrenadeSelectionIsEmptyOrUsesDistanceThenCoordinates()
    {
        Assert.Null(CombatGrenadeRules.BestGround(new(0, 0), 20, 1, [], [], []));
        Cell[] cells = [new(0, 10), new(10, 0), new(0, 9)];
        Assert.Equal(new Cell(0, 9), CombatGrenadeRules.BestGround(new(0, 0), 20, 1, cells, cells, []));
        cells = [new(10, 0), new(0, 10)];
        Assert.Equal(new Cell(0, 10), CombatGrenadeRules.BestGround(new(0, 0), 20, 1, cells, cells, []));
    }
}