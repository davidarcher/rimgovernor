using RimGovernor.Brain.Bridge;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3540: the Human BodyDef's coverageAbs summed per apparel body-part group set, and the apparel layer order.
public sealed class BodyCoverageTests
{
    private static readonly BodyPartGroupDef Torso = new() { defName = "Torso" };
    private static readonly BodyPartGroupDef FullHead = new() { defName = "FullHead" };
    private static readonly BodyPartGroupDef UpperHead = new() { defName = "UpperHead" };

    [Fact]
    public void GroupProbabilitiesAndTheUncoveredRemainderSumToOne()
    {
        var coverage = Catalog().BodyCoverage();

        Assert.Equal(1, coverage.Shares.Sum(s => s.Probability) + coverage.Uncovered, 9);
        Assert.Equal(0.1, coverage.Uncovered, 9);
    }

    [Fact]
    public void APartInTwoGroupsIsOneShareKeyedByItsGroupSet()
    {
        var coverage = Catalog().BodyCoverage();

        Assert.Equal(["FullHead|UpperHead", "Torso"], coverage.Shares.Select(s => string.Join('|', s.Groups)));
        Assert.Equal([0.15, 0.75], coverage.Shares.Select(s => Math.Round(s.Probability, 9)));
    }

    [Fact]
    public void LayersRunInnermostFirstByDrawOrder()
    {
        Assert.Equal(["OnSkin", "Middle", "Shell"], Catalog().BodyCoverage().Layers);
    }

    private static DefinitionCatalog Catalog()
    {
        var body = new BodyDef
        {
            defName = nameof(BodyDefOf.Human),
            corePart = new BodyPartRecord
            {
                def = new BodyPartDef { defName = "Torso" },
                coverageAbs = 0.5f,
                groups = [Torso],
                parts =
                [
                    new BodyPartRecord { def = new BodyPartDef { defName = "Skull" }, coverageAbs = 0.1f, groups = [UpperHead, FullHead], parts = [] },
                    new BodyPartRecord { def = new BodyPartDef { defName = "Jaw" }, coverageAbs = 0.05f, groups = [FullHead, UpperHead], parts = [] },
                    new BodyPartRecord { def = new BodyPartDef { defName = "Arm" }, coverageAbs = 0.25f, groups = [Torso], parts = [] },
                    new BodyPartRecord { def = new BodyPartDef { defName = "Tail" }, coverageAbs = 0.1f, groups = [], parts = [] },
                ],
            },
        };
        return new(
            [
                new ThingDef { defName = "Silver" },
                new TerrainDef { defName = "Soil" },
                body,
                new ApparelLayerDef { defName = "Shell", drawOrder = 40 },
                new ApparelLayerDef { defName = "OnSkin", drawOrder = 10 },
                new ApparelLayerDef { defName = "Middle", drawOrder = 30 },
            ],
            DefStatTable.Empty,
            new CatalogDerived("Silver", "Wort", 10f));
    }
}
