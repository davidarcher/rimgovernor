using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A pawn's health read is total (#3770): a hediff's part is a <see cref="BodyPartRef"/> or whole-body, its tend
/// is untended or tended at a quality, an immunizable hediff carries its immunity and <see cref="ImmunityGain"/>,
/// the blood-loss death is a <see cref="BleedOut"/> and an operation's chance a <see cref="SurgeryChance"/>. It
/// round-trips through <c>WireJsonContext</c> exactly.
/// </summary>
public sealed class HealthWireTests
{
    private static readonly BodyPartRef Leg = new() { Index = 7, DefName = "Leg" };

    private static readonly PawnHealth Health = PawnFixtures.Healthy with
    {
        Bleeding = true,
        BleedOut = new BleedOut.Within(5.5),
        Hediffs =
        [
            new Hediff { DefName = "Cut", Part = Leg, Severity = 3, Visible = true, Tend = new HediffTend.Tended(0.75), Immunizable = null },
            new Hediff
            {
                DefName = "Flu", Part = null, Severity = 0.2, Visible = true, Tend = new HediffTend.Untended(),
                Immunizable = new HediffImmunity { Immunity = 0.1, SeverityPerDay = 0.5, Gain = new ImmunityGain.PerDay(0.3) },
            },
            new Hediff
            {
                DefName = "Plague", Part = null, Severity = 0.4, Visible = true, Tend = new HediffTend.Untended(),
                Immunizable = new HediffImmunity { Immunity = 0, SeverityPerDay = 0.6, Gain = new ImmunityGain.NoRecord() },
            },
        ],
        BedId = "Bed_1",
        Surgery = new PawnSurgery
        {
            SurgeryBills = [new SurgeryBill { Id = "Bill_1", Recipe = "InstallPegLeg", Part = Leg }],
            MissingParts = [new MissingBodyPart { PartIndex = 7, PartDefName = "Leg" }],
            Operations =
            [
                new SurgeryOperation
                {
                    Recipe = new DefinitionRef { DefName = "InstallPegLeg" }, Part = Leg, Kind = SurgeryKind.Restore,
                    Chance = new SurgeryChance.Rated(0.9, 0.95, new Dictionary<string, double> { ["doc"] = 0.9 }),
                    EligibleDoctors = 1, IngredientsOnMap = true, Violation = false, Lethal = false, Yield = null, AddedPartHediff = null,
                    MedicineMarketValue = 18, MedicineCareLimited = false,
                },
                new SurgeryOperation
                {
                    Recipe = new DefinitionRef { DefName = "HarvestKidney" }, Part = new BodyPartRef { Index = 12, DefName = "Kidney" }, Kind = SurgeryKind.Harvest,
                    Chance = new SurgeryChance.NoEligibleDoctor(),
                    EligibleDoctors = 0, IngredientsOnMap = false, Violation = true, Lethal = false,
                    Yield = new SurgeryYield { ThingDef = "Kidney", MarketValue = 250 }, AddedPartHediff = null,
                    MedicineMarketValue = null, MedicineCareLimited = false,
                },
                new SurgeryOperation
                {
                    Recipe = new DefinitionRef { DefName = "Euthanize" }, Part = null, Kind = SurgeryKind.Other,
                    Chance = new SurgeryChance.NoOutcomeEffect(),
                    EligibleDoctors = 1, IngredientsOnMap = true, Violation = false, Lethal = true, Yield = null, AddedPartHediff = null,
                    MedicineMarketValue = null, MedicineCareLimited = false,
                },
            ],
        },
    };

    [Fact]
    public void AHealthReadRoundTripsExactly()
    {
        var json = WireJson.Serialize(Health);

        Assert.Contains("""{"defName":"Cut","part":{"index":7,"defName":"Leg"},"severity":3,"visible":true,"tend":{"type":"tended","quality":0.75},"immunizable":null}""", json);
        Assert.Contains("""{"defName":"Flu","part":null,"severity":0.2,"visible":true,"tend":{"type":"untended"},"immunizable":{"immunity":0.1,"severityPerDay":0.5,"gain":{"type":"per_day","value":0.3}}}""", json);
        Assert.Contains("""{"type":"no_record"}""", json);
        Assert.Contains("""{"type":"within","hours":5.5}""", json);
        Assert.Contains("""{"type":"rated","success":0.9,"idealSuccess":0.95,"doctors":{"doc":0.9}}""", json);
        Assert.Contains("""{"type":"no_eligible_doctor"}""", json);
        Assert.Contains("""{"type":"no_outcome_effect"}""", json);
        Assert.Contains("""{"thingDef":"Kidney","marketValue":250}""", json);
        Assert.Contains("\"kind\":\"restore\"", json);
        Assert.Contains("\"kind\":\"harvest\"", json);
        Assert.Contains("""{"id":"Bill_1","recipe":"InstallPegLeg","part":{"index":7,"defName":"Leg"}}""", json);
        Assert.Equal(json, WireJson.Serialize(WireJson.Deserialize<PawnHealth>(json)));
    }

    [Fact]
    public void AnUnspecifiedSurgeryKindDoesNotDecode()
    {
        var json = WireJson.Serialize(Health).Replace("\"kind\":\"restore\"", "\"kind\":\"SURGERY_KIND_UNSPECIFIED\"", StringComparison.Ordinal);

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => WireJson.Deserialize<PawnHealth>(json));
    }
}
