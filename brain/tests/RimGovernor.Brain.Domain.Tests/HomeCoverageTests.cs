using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The Home census is one total facts record (#3771): the revision, the auto-expand setting, the Home cells and one row per
/// target, a target with no bounded visible geometry named <see cref="HomeCoverageTarget.Unscoped"/>. On the wire it
/// round-trips through <c>WireJsonContext</c> exactly.
/// </summary>
public sealed class HomeCoverageTests
{
    /// <summary>A census with no Home cells and no targets, auto-expand off: the base every test Home census starts from.</summary>
    internal static readonly HomeCoverageFacts NoHomeCoverage = new() { Revision = 0, AutoHomeArea = false, HomeCells = [], Targets = [] };

    private static readonly HomeExtentGeometry NoExtent = new([], [], []);

    [Fact]
    public void JsonRoundTripIsExact()
    {
        var census = NoHomeCoverage with
        {
            Revision = 7,
            AutoHomeArea = true,
            HomeCells = [new(1, 2)],
            Targets =
            [
                new HomeCoverageTarget.Scoped("Thing_Bed1", new([new(2, 3)], [new(2, 4)], [])),
                new HomeCoverageTarget.Unscoped("Thing_Wall2"),
            ],
        };

        var json = WireJson.Serialize(census);

        Assert.Equal(
            """{"revision":7,"autoHomeArea":true,"homeCells":[{"x":1,"z":2}],"targets":[{"type":"scoped","extent":{"enclosedInterior":[{"x":2,"z":3}],"corridor":[{"x":2,"z":4}],"zone":[]},"id":"Thing_Bed1"},{"type":"unscoped","id":"Thing_Wall2"}]}""",
            json);
        Assert.Equal(census, WireJson.Deserialize<HomeCoverageFacts>(json));
    }

    /// <summary>
    /// A stockpile is colony territory at its whole footprint; a target without bounded visible geometry adds nothing to the
    /// extent by its named state (#3771), and with no census read the extent is unknown.
    /// </summary>
    [Fact]
    public void AnUnscopedTargetAddsNoTerritory()
    {
        var request = new ColonyExtentRequest
        {
            Bounds = new Bounds(10, 10),
            Construction = CurrentConstruction.Empty,
            Claims = [],
            Home = NoHomeCoverage with
            {
                Targets =
                [
                    new HomeCoverageTarget.Scoped("Zone_Stockpile1", NoExtent with { Zone = [new(1, 1), new(1, 2)] }),
                    new HomeCoverageTarget.Unscoped("Zone_Stockpile2"),
                ],
            },
        };

        var extent = Assert.IsType<ExtentDerivation.Derived>(ColonyExtentRules.DeriveColonyExtent(request)).Extent;
        var region = Assert.Single(extent.Regions);
        Assert.Equal([new Cell(1, 1), new Cell(1, 2)], region.Cells.Select(c => c.Cell));
        Assert.Equal(new ExtentDerivation.Incomplete("home coverage census"), ColonyExtentRules.DeriveColonyExtent(request.Bounds, request.Construction, request.Claims, null));
    }
}
