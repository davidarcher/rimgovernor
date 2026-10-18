using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3559: a real review's tier-2 graph carries the devilstrand payoff chain (research gate, crop, craft node), so the harm ledger prices
// it. The frame is a staged devilstrand colony: bench and researcher Anna (Plants 10), Devilstrand the current project, a growing zone
// of devilstrand and a bill option for the duster in devilstrand cloth.
public sealed class ColonyModelWiringTests
{
    private static GearOption BillDuster() => ForecastValueTests.Duster(0.8) with
    {
        ID = "bill:Apparel_Duster/DevilstrandCloth", Definition = new Resource("Apparel_Duster"), Stuff = new Resource(ColonyModelFixtures.Cloth), Source = GearSource.Bill,
    };

    private static ColonyProjection Frame()
    {
        var projection = ColonyModelFixtures.Projection();
        var gear = projection.Facts.Gear.Value;
        return projection with
        {
            Facts = projection.Facts with
            {
                Gear = Known(gear with { Pawns = [gear.Pawns[0] with { Climate = ColonyModelFixtures.Climate() with { Temperatures = EquatableArray.From(new[] { -10.0, -8, -2, 5, 12, 18, 22, 21, 15, 8, 12, 12 }) }, LoadoutModel = Known(new GearLoadoutInput { Options = [BillDuster()] }) }] }),
                ForecastObserved = new ColonyForecastObserved
                {
                    Coupled = new CoupledForecastRead
                    {
                        Research = new ResearchActivityRead { Known = true, Workers = [new ResearchWorker("Anna", "bench", default, ColonyModelFixtures.Project, "", "", Known(ProjectGraph.ResearchPointsPerWorkTick))] },
                        Sowing = new SowingActivityRead { Known = true, Workers = [new SowingWorker("Anna", "zone1", "", Known(0.0), Known(400.0), [])] },
                    },
                },
            },
            // 20 cells of 6 cloth each cover the duster's 80.
            Cells = [.. Enumerable.Range(0, 20).Select(x => new SiteCell { Cell = new Cell(x, 0), Zone = Known(true), ZoneId = Known("zone1"), Fertility = Known(0.8) })],
            CombinedFoodSupply = Known(new FoodSupply { Complete = Known(true), Stocks = [new RimGovernor.Brain.Policy.FoodStock { Id = "meal", Nutrition = Known(10_000.0) }] }),
        };
    }

    private static ForecastCoupledContext Context(HarmInputs harm)
    {
        var anna = ForecastValueTests.HarmPawn() with { Pawn = "Anna" };
        return ForecastValueTests.Context(anna, ForecastValueTests.HarmTables(), harm, new Dictionary<string, EquatableArray<GearOption>> { ["Anna"] = ForecastValueTests.Cloth });
    }

    [Fact]
    public void AReviewBuildsTheResearchGateTheCropChainAndTheCraftNodeAndPricesThemByTheHarmLedger()
    {
        var review = RoundsColonyModel.Review(Frame(), Context(ForecastValueTests.RaidDay()), null);
        var ids = review.Marginals.Trajectory.Events.Select(e => e.Id).Where(id => id.Length > 0).ToArray();

        Assert.Equal(["research:Devilstrand", "sow:zone1", "grow:zone1", "harvest:zone1", "craft:" + ColonyModelFixtures.Recipe], ids);
        Assert.DoesNotContain(ColonyValue.ApparelHarmUnassessed, review.Marginals.Findings);
        Assert.True(review.Marginals.PerHour["research:Devilstrand"] > 0);
        Assert.True(review.Marginals.PerHour["sow:zone1"] > 0);
        Assert.True(review.Marginals.PerUnit["Devilstrand"] > 0);
    }

    [Fact]
    public void WhileTheExposureTableIsAPlaceholderTheNodeExistsHarmIsUnassessedAndTheInvestmentIsZero()
    {
        var review = RoundsColonyModel.Review(Frame(), Context(new HarmInputs()), null);

        Assert.True(review.Marginals.Trajectory.Events.Any(e => e.Id == "craft:" + ColonyModelFixtures.Recipe), string.Join(',', review.Marginals.Findings));
        Assert.Contains(ColonyValue.ApparelHarmUnassessed, review.Marginals.Findings);
        Assert.Equal(0, review.Marginals.PerHour["research:Devilstrand"]);
        Assert.Equal(0, review.Marginals.PerUnit["Devilstrand"]);
    }

    [Fact]
    public void TheGarmentIsTheBillOptionOfTheCropsStuffTheGearPlannerScoresHighest()
    {
        var crop = RoundsFieldPlanner.CropChoices(Frame()).Single();
        var better = BillDuster() with { ID = "bill:Apparel_Parka/DevilstrandCloth", Definition = new Resource("Apparel_Parka"), Sharp = 0.9 };
        var cotton = BillDuster() with { ID = "bill:Apparel_Duster/Cloth", Stuff = new Resource("Cloth"), Sharp = 0.99 };

        Assert.Equal(["Devilstrand"], crop.Research.Select(r => r.Value));
        Assert.Equal(better.ID, ProjectGraph.Garment(crop, [BillDuster(), better, cotton], default)?.ID);
        Assert.Null(ProjectGraph.Garment(crop, [cotton], default));
        Assert.Null(ProjectGraph.Garment(crop, [BillDuster() with { Sharp = 0, Blunt = 0 }], default));
    }
}
