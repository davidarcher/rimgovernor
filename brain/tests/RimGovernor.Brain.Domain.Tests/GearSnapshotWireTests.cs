using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The gear census is whole on the wire (#3777): every member is required, an item's quality is a named state
/// (<see cref="RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded"/> or <see cref="RimGovernor.Brain.GameFacts.Observations.ItemQuality.Qualityless"/>) and an option's source an enum, and
/// the census round-trips exactly.
/// </summary>
public sealed class GearSnapshotWireTests
{
    private static RimGovernor.Brain.GameFacts.Observations.GearSnapshot Census()
    {
        var graded = new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory.Good);
        var plain = new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Qualityless();
        var loadout = GearFixtures.Loadout("Thing_Human1") with
        {
            Candidates = [new RimGovernor.Brain.GameFacts.Observations.GearCandidate { Item = GearFixtures.Item("Apparel_2", plain), Gain = 0.25 }],
            BodyPartGroups = ["Torso", "Legs"],
            LoadoutModel = GearFixtures.NoModel with
            {
                Worn = [GearFixtures.Option("Apparel_1", "Apparel_TribalA", "Cloth", GearSource.Worn) with { Quality = graded, Locked = true }],
                Options =
                [
                    GearFixtures.Option("Apparel_2", "Apparel_Parka", null, GearSource.Loose) with { Quality = plain, Tainted = true, Condition = 0.75 },
                    GearFixtures.Option("Apparel_3", "Apparel_Pants", "Cloth", GearSource.Stored),
                    GearFixtures.Option("bill:Apparel_Duster/Cloth", "Apparel_Duster", "Cloth", GearSource.Bill) with { Crafters = [new RimGovernor.Brain.GameFacts.Observations.GearCrafter(6, true, 1)] },
                ],
            },
        };
        return GearFixtures.NoSnapshot with
        {
            Pawns = [loadout, GearFixtures.Loadout("Thing_Human2") with { Blocked = true, Gender = RimGovernor.Brain.GameFacts.Defs.Gender.Female, DevelopmentalStage = RimGovernor.Brain.GameFacts.Defs.DevelopmentalStage.Child }],
            StoredApparel =
            [
                new RimGovernor.Brain.GameFacts.Observations.GearStock { DefName = "Apparel_Pants", Stuff = "Cloth", Quality = graded, HpBand = 9, Count = 2 },
                new RimGovernor.Brain.GameFacts.Observations.GearStock { DefName = "Apparel_Parka", Stuff = null, Quality = plain, HpBand = 6, Count = 1 },
            ],
            LockedBills =
            [
                new RimGovernor.Brain.GameFacts.Observations.GearLockedBill { DefName = "Apparel_PowerArmor", Craftable = false, Crafters = [] },
                new RimGovernor.Brain.GameFacts.Observations.GearLockedBill { DefName = "Apparel_Cape", Craftable = true, Crafters = [new RimGovernor.Brain.GameFacts.Observations.GearCrafter(10, false, 0)] },
            ],
        };
    }

    [Fact]
    public void TheGearCensusRoundTripsExactly()
    {
        var census = Census();
        var json = OrdersJson.Serialize(census);
        var back = OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.GearSnapshot>(json);
        Assert.Equal(json, OrdersJson.Serialize(back));
        Assert.Equal(census, back);
        Assert.Contains("\"qualityless\"", json);
        Assert.Contains("\"graded\"", json);
        Assert.Contains("\"stored\"", json);
    }

    [Fact]
    public void AnItemWithoutItsQualityDoesNotDecode()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(OrdersJson.Serialize(GearFixtures.Item("Apparel_1", new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Qualityless())))!.AsObject();
        node.Remove(node.Select(p => p.Key).Single(k => k.Equals("quality", StringComparison.OrdinalIgnoreCase)));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.GearItem>(node.ToJsonString()));
    }
}
