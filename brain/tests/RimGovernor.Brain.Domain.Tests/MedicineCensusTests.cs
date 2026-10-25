using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Bridge.Contracts;
using RimWorld;
using Verse;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class MedicineCensusTests
{
    [Fact]
    public void ReserveRetainsActualNativeMedicineRowsAndCensus()
    {
        var medicine = new ThingDef
        {
            defName = "MedicineHerbal",
            statBases = [new StatModifier { stat = new StatDef { defName = nameof(StatDefOf.MedicalPotency) }, value = .7f }],
        };
        var catalog = new DefinitionCatalog(
            [medicine, new ThingDef { defName = "Silver" }, new TerrainDef { defName = "Soil" }],
            DefStatTable.Empty, new CatalogDerived("Silver", "Wort", 10f));
        var row = new O.UpkeepItem
        {
            Id = "medicine", Definition = medicine.defName, Count = 4,
            Forbidden = true, Rot = new O.ItemRot.Rotting(120),
        };
        var census = ColonyFactsCoreTests.Snapshot();
        census = census with { Upkeep = census.Upkeep with { Items = [row] } };

        var reserve = ColonyFramesReads.ColonyMedicalReserve(census, new Tables(default, default, default, []), catalog);

        Assert.Same(census, reserve.Census);
        Assert.Same(row, Assert.Single(reserve.Items));
        Assert.Same(row.Rot, reserve.Items[0].Rot);
    }
}
