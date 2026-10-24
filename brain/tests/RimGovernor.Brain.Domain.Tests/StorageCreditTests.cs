using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using LootItem = RimGovernor.Brain.Policy.LootItem;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3798: the shipped storage credit. Room 1 (PlannedRoomCreditTests' storage room, serving from day three of a ten-day
/// horizon) would keep 200 steel and 50 cloth lying unroofed in open air on a 250x250 map without rain. Steel cannot
/// deteriorate (native reads no deterioration for it); the cloth loses the game's expected hit points a day there and
/// none in the roofed indoor room, at its count × value per unit over its MaxHitPoints a hit point.
/// </summary>
public sealed class StorageCreditTests
{
    private const double Horizon = 10, Served = 3, PerUnit = 0.01;
    private const int Area = 62500;
    private const string Cloth = "Thing_Cloth1", Steel = "Thing_Steel1", Rice = "Thing_RawRice1";

    private static LootDeterioration OpenAir(double rate, double maxHitPoints = 100) =>
        new() { MaxHitPoints = maxHitPoints, RatePerDay = rate, Environmental = true, Roofed = false, Outdoors = true };

    private static LootItem Row(string thing, string def, long count, LootDeterioration? deterioration) => new()
    {
        Supply = new StartingSupply { Thing = thing, Definition = def, Cell = new(20, 20) },
        Count = count, SafeToHaul = true, SafetyKnown = true, StorageHeadroom = Known(0L), Deterioration = deterioration,
    };

    /// <summary>The cloth's credit a served day: its rate (2 a day, StatPart factor 1 now, 0 roofed indoors) over 36 a visit, the visits a tick and the ticks a day, at its value per hit point.</summary>
    private static readonly double ClothPerDay = 2.0 / 36 * (Math.Ceiling(Area * 0.0006) / Area) * GameTime.TicksPerDay * 50 * PerUnit / 100;

    private static ForecastCoupledContext Context(IReadOnlyList<LootItem> rows, int cells = 9, bool cloth = true, NeedTarget? food = null)
    {
        var context = PlannedRoomCreditTests.Context(PlannedRoomCredits.Shipped);
        var pawn = context.Needs.Pawns[0];
        var values = new Dictionary<Resource, ConstructionMaterialValue> { [new("WoodLog")] = new(MaterialValueBasis.Labor, 0.001) };
        if (cloth) values[new("Cloth")] = new(MaterialValueBasis.Market, PerUnit);
        return context with
        {
            Needs = context.Needs with
            {
                RainRate = 0d, MapArea = Area, Deterioration = new(.5, .5, 0),
                Pawns = [food is null ? pawn : pawn with { Targets = [food] }],
            },
            StoreStock = new Dictionary<int, PlannedStock> { [1] = new(cells, EquatableArray.From(rows)) },
            MaterialValues = values,
        };
    }

    private static readonly LootItem[] SteelAndCloth = [Row(Steel, "Steel", 200, null), Row(Cloth, "Cloth", 50, OpenAir(2))];

    private static ForecastValueResult Ledger(ForecastCoupledContext context) =>
        ForecastValue.Of(PlannedRoomCreditTests.Outcome(context), context, GameTime.TicksPerDay);

    private static ForecastInvestmentContribution[] Credits(ForecastValueResult ledger) =>
        [.. ledger.InvestmentContributions.Where(r => r.Key.StartsWith("storage/", StringComparison.Ordinal))];

    [Fact]
    public void SteelAndClothLyingUnroofedAreCreditedTheClothsHitPointLossAtItsValue()
    {
        var ledger = Ledger(Context(SteelAndCloth));
        var row = Assert.Single(Credits(ledger));
        Assert.Equal(("", "storage/1/" + Cloth, Horizon - Served), (row.Pawn, row.Key, row.Units));
        Assert.Equal(-ClothPerDay * (Horizon - Served), row.Value, 12);
        Assert.DoesNotContain("storage_credit_unassessed:1", ledger.Findings);
        // The room's stock is priced with the materials it is charged.
        Assert.Contains(new Resource("Cloth"), ForecastValue.ChargedMaterials(Context(SteelAndCloth)));
        Assert.DoesNotContain(new Resource("Steel"), ForecastValue.ChargedMaterials(Context(SteelAndCloth)));
    }

    [Fact]
    public void AFoodLotHungerAlreadyDebitsAddsNothing()
    {
        var food = NeedsFixtures.Target(Rice, "food", "RawRice", 10, 0d, new NeedRecovery.Rate(0)) with { Deterioration = new(70, 1d, true, 0, false) };
        var rows = SteelAndCloth.Append(Row(Rice, "RawRice", 75, OpenAir(1, 70))).ToArray();
        var credited = Credits(Ledger(Context(rows, food: food)));
        Assert.Equal(["storage/1/" + Cloth], credited.Select(r => r.Key));
        // The same lot no pawn would eat is the room's stock (its value unpriced here, so the room is unassessed).
        Assert.Contains("storage_credit_unassessed:1", Ledger(Context(rows)).Findings);
    }

    [Fact]
    public void AnUnknownStackValueLeavesTheStorageCreditUnassessed()
    {
        var ledger = Ledger(Context(SteelAndCloth, cloth: false));
        Assert.Contains("storage_credit_unassessed:1", ledger.Findings);
        Assert.Empty(Credits(ledger));
        // An unread stock is unassessed too, never thrown.
        ledger = Ledger(Context(SteelAndCloth) with { StoreStock = null });
        Assert.Contains("storage_credit_unassessed:1", ledger.Findings);
        Assert.Empty(Credits(ledger));
    }

    [Fact]
    public void TheRoomHoldsAsManyStacksAsItsCellsTheWorstFirst()
    {
        // Two cloth stacks, one cell: the one losing more is kept.
        var worse = Row("Thing_Cloth2", "Cloth", 50, OpenAir(4));
        Assert.Equal(["storage/1/Thing_Cloth2"], Credits(Ledger(Context([.. SteelAndCloth, worse], cells: 1))).Select(r => r.Key));
        // A stack already roofed indoors gains nothing.
        var sheltered = Row(Cloth, "Cloth", 50, OpenAir(2) with { Roofed = true, Outdoors = false });
        Assert.Empty(Credits(Ledger(Context([sheltered]))));
    }

    [Fact]
    public void AYardIsCreditedOnlyForStockItsCellsProtectMore()
    {
        var context = Context(SteelAndCloth);
        var yard = context with { PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Yard, [2] = PlannedRole.Workshop } };
        var ledger = Ledger(yard);
        Assert.DoesNotContain(ledger.InvestmentContributions, r => r.Key.StartsWith("yard/", StringComparison.Ordinal));
        Assert.DoesNotContain("yard_credit_unassessed:1", ledger.Findings);
    }

    [Fact]
    public void TheStorageRoomLeavesTheDevelopmentCount()
    {
        // PlannedRoomCreditTests' count: eight units unpriced, two once the room's role has a credit, now the shipped one.
        Assert.True(Context(SteelAndCloth).Priced(1));
        Assert.Equal(2, PlannedRoomCreditTests.Development(Context(SteelAndCloth)));
    }
}
