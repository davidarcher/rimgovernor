using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The delivery ledger is one total facts record (#3763): every member is <c>required</c>, so a ledger built without
/// one (say, without <c>Kills</c>) does not compile (CS9035), and native cannot hand the planner a partial ledger.
/// On the wire it round-trips through <c>WireJsonContext</c> exactly.
/// </summary>
public sealed class DeliveryLedgerTests
{
    /// <summary>A colony that has delivered nothing yet in this load: the snapshot every test without deliveries carries.</summary>
    internal static readonly DeliveryLedger NoDeliveries = new() { LoadToken = "load", Counts = [], Kills = [], Butchers = [] };

    [Fact]
    public void JsonRoundTripIsExact()
    {
        var ledger = new DeliveryLedger
        {
            LoadToken = "9f2c",
            Counts =
            [
                new DeliveryCount { Kind = DeliveryKind.Crop, Source = "Zone_Growing12", Def = "RawRice", Units = 0, Nutrition = 0 },
                new DeliveryCount { Kind = DeliveryKind.AnimalProduct, Source = "Cow", Def = "Milk", Units = 14, Nutrition = 0.7 },
            ],
            Kills = [new Kill { CorpseId = "Thing_Corpse_Deer88", Pawn = new PawnId("Thing_Deer87"), Race = "Deer", BodySize = 1.2, PotentialNutrition = 3.15, Tick = new Tick(61234) }],
            Butchers = [new Butcher { CorpseId = "Thing_Corpse_Deer88", MeatUnits = 50, MeatNutrition = 2.5, LeatherUnits = 0 }],
        };

        var json = WireJson.Serialize(ledger);

        Assert.Equal(
            """{"loadToken":"9f2c","counts":[{"kind":"crop","source":"Zone_Growing12","def":"RawRice","units":0,"nutrition":0},{"kind":"animal_product","source":"Cow","def":"Milk","units":14,"nutrition":0.7}],"kills":[{"corpseId":"Thing_Corpse_Deer88","pawn":"Thing_Deer87","race":"Deer","bodySize":1.2,"potentialNutrition":3.15,"tick":61234}],"butchers":[{"corpseId":"Thing_Corpse_Deer88","meatUnits":50,"meatNutrition":2.5,"leatherUnits":0}]}""",
            json);
        Assert.Equal(ledger, WireJson.Deserialize<DeliveryLedger>(json));
    }
}
