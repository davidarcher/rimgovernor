using RimGovernor.Brain.Bridge;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3185: a Core-only load has gene defs but no MechWorkModeDef; Biotech is then absent, not a CatalogException.
public sealed class BiotechCatalogTests
{
    [Fact]
    public void CoreOnlyLoadWithGenesButNoMechWorkModesHasNoBiotech()
    {
        var catalog = Catalog(new GeneDef { defName = "Robust" });

        Assert.Null(catalog.Biotech);
    }

    private static DefinitionCatalog Catalog(params Def[] extra) =>
        new(
            [
                new ThingDef { defName = "Silver" },
                new TerrainDef { defName = "Soil" },
                .. extra,
            ],
            DefStatTable.Empty,
            new CatalogDerived("Silver", "Wort", 10f));
}
