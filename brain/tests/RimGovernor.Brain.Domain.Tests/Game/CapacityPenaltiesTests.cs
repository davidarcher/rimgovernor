using System.Reflection;
using RimGovernor.Brain.Bridge;
using RimWorld;
using Verse;
using Xunit;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3519, #3521: the capacity penalty tables decoded from the malnutrition, food poisoning, hypothermia and heatstroke defs, shaped as the 1.7 Core defs state them.
public sealed class CapacityPenaltiesTests
{
    private static readonly Dictionary<string, PawnCapacityDef> Capacities =
        typeof(PawnCapacityDefOf).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.Name)
            .Append("Eating").ToDictionary(n => n, n => new PawnCapacityDef { defName = n });

    [Fact]
    public void TheTablesHaveStagesOrderedBySeverityAndCapacitiesNamedByDefOfFields()
    {
        var tables = Catalog(Malnutrition(), FoodPoisoning(), Thermal(nameof(HediffDefOf.Hypothermia)), Thermal(nameof(HediffDefOf.Heatstroke)), Moving()).CapacityPenalties();

        Assert.True(tables.Valid());
        foreach (var table in new[] { tables.Malnutrition, tables.FoodPoisoning, tables.Hypothermia, tables.Heatstroke })
        {
            Assert.True(table.Known);
            Assert.Equal(table.Stages.Select(s => s.MinSeverity).Order(), table.Stages.Select(s => s.MinSeverity));
            var fields = typeof(PawnCapacityDefOf).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.Name).ToHashSet();
            Assert.All(table.Stages.SelectMany(s => s.Modifiers), m => Assert.Contains(m.Capacity, fields));
        }

        Assert.Equal([0.0, 0.2, 0.4, 0.6, 0.8], tables.Malnutrition.Stages.Select(s => s.MinSeverity));
        Assert.Equal(1, tables.Malnutrition.LethalSeverity);
        Assert.Equal(new P.CapacityModifier(nameof(PawnCapacityDefOf.Consciousness), 0, 0.1, 1), Assert.Single(tables.Malnutrition.Stages[^1].Modifiers));
        Assert.Equal(0.15, tables.MovingMinForCapable);
    }

    [Fact]
    public void FoodPoisoningCarriesItsSeverityDynamicsAndLeavesOutTheEatingCapacity()
    {
        var table = Catalog(FoodPoisoning()).CapacityPenalties().FoodPoisoning;

        Assert.Equal(1, table.InitialSeverity);
        Assert.Equal(-1, table.SeverityPerDay);
        Assert.Null(table.LethalSeverity);
        Assert.Null(table.DisappearsAfterTicks);
        Assert.Equal(
            [nameof(PawnCapacityDefOf.Consciousness), nameof(PawnCapacityDefOf.Moving)],
            table.Stages[0].Modifiers.Select(m => m.Capacity));
        Assert.Equal(0.6, table.Stages[0].Modifiers[0].PostFactor);
        Assert.Null(table.Stages[0].Modifiers[0].SetMax);
    }

    [Fact]
    public void AMissingDefIsAnUnknownTableNotAnEmptyOne()
    {
        var tables = Catalog(Malnutrition()).CapacityPenalties();

        Assert.True(tables.Malnutrition.Valid());
        Assert.False(tables.FoodPoisoning.Known);
        Assert.False(tables.FoodPoisoning.Valid());
        Assert.Null(tables.MovingMinForCapable);
        Assert.False(tables.Valid());
        Assert.False(new P.CapacityPenaltyTables().Malnutrition.Valid());
        Assert.False((new P.HediffPenaltyTable { Known = true, Hediff = "Malnutrition" }).Valid());
    }

    [Fact]
    public void BloodLossAndWoundInfectionAreDecodedWithTheirCompProps()
    {
        var tables = Catalog(
            new HediffDef { defName = nameof(HediffDefOf.BloodLoss), lethalSeverity = 1, stages = [Stage(0, 0, Mod("Consciousness", postFactor: 0.9f)), Stage(0.6f, 0, Mod("Consciousness", setMax: 0.2f))] },
            new HediffDef
            {
                defName = nameof(HediffDefOf.WoundInfection),
                lethalSeverity = 1,
                initialSeverity = 0.2f,
                comps =
                [
                    new HediffCompProperties_Immunizable { immunityPerDaySick = 0.5f, severityPerDayNotImmune = 0.25f, severityPerDayImmune = -0.5f },
                    new HediffCompProperties_TendDuration { severityPerDayTended = -0.75f },
                ],
                stages = [Stage(0, 0, Mod("Consciousness", postFactor: 0.8f))],
            }).CapacityPenalties();

        Assert.True(tables.BloodLoss.Valid());
        Assert.Equal(1, tables.BloodLoss.LethalSeverity);
        Assert.Equal([0.0, 0.6], tables.BloodLoss.Stages.Select(s => s.MinSeverity));
        Assert.Null(tables.BloodLoss.Immunizable);
        Assert.Null(tables.BloodLoss.SeverityPerDayTended);
        Assert.True(tables.WoundInfection.Valid());
        Assert.Equal(new P.ImmunizableProps(0.5, 0.25, -0.5), tables.WoundInfection.Immunizable);
        Assert.Equal(-0.75, tables.WoundInfection.SeverityPerDayTended);
        Assert.Equal(0.2, tables.WoundInfection.InitialSeverity);
        Assert.False(Catalog(Malnutrition()).CapacityPenalties().BloodLoss.Known);
    }

    [Fact]
    public void StagesOutOfOrderInTheDefAreOrdered()
    {
        var def = Malnutrition();
        def.stages.Reverse();

        Assert.Equal([0.0, 0.2, 0.4, 0.6, 0.8], Catalog(def).CapacityPenalties().Malnutrition.Stages.Select(s => s.MinSeverity));
    }

    private static DefinitionCatalog Catalog(params Def[] defs) =>
        new(
            [new ThingDef { defName = "Silver" }, new TerrainDef { defName = "Soil" }, .. defs],
            DefStatTable.Empty,
            new CatalogDerived("Silver", "Wort", 10f));

    private static PawnCapacityDef Moving() => new() { defName = nameof(PawnCapacityDefOf.Moving), minForCapable = 0.15f };

    private static PawnCapacityModifier Mod(string capacity, float offset = 0, float? setMax = null, float postFactor = 1) =>
        new() { capacity = Capacities[capacity], offset = offset, setMax = setMax ?? 999f, postFactor = postFactor };

    private static HediffStage Stage(float min, float pain = 0, params PawnCapacityModifier[] mods) =>
        new() { minSeverity = min, painOffset = pain, capMods = [.. mods] };

    private static HediffDef Malnutrition() => new()
    {
        defName = nameof(HediffDefOf.Malnutrition),
        lethalSeverity = 1,
        stages =
        [
            Stage(0, 0, Mod("Consciousness", -0.05f)),
            Stage(0.2f, 0, Mod("Consciousness", -0.10f)),
            Stage(0.4f, 0, Mod("Consciousness", -0.20f)),
            Stage(0.6f, 0, Mod("Consciousness", -0.30f)),
            Stage(0.8f, 0, Mod("Consciousness", setMax: 0.1f)),
        ],
    };

    private static HediffDef Thermal(string name) => new()
    {
        defName = name,
        lethalSeverity = 1,
        stages = [Stage(0, 0, Mod("Moving", postFactor: 0.9f)), Stage(0.35f, 0.1f, Mod("Consciousness", -0.1f), Mod("Moving", postFactor: 0.5f))],
    };

    private static HediffDef FoodPoisoning() => new()
    {
        defName = nameof(HediffDefOf.FoodPoisoning),
        initialSeverity = 1,
        comps = [new HediffCompProperties_SeverityPerDay { severityPerDay = -1 }],
        stages =
        [
            Stage(0, 0.2f, Mod("Consciousness", postFactor: 0.6f), Mod("Moving", postFactor: 0.8f), Mod("Eating", postFactor: 0.5f)),
            Stage(0.2f, 0.4f, Mod("Consciousness", postFactor: 0.5f), Mod("Moving", postFactor: 0.5f), Mod("Eating", postFactor: 0.3f)),
            Stage(0.8f, 0.2f, Mod("Consciousness", postFactor: 0.6f), Mod("Eating", postFactor: 0.5f)),
        ],
    };
}
