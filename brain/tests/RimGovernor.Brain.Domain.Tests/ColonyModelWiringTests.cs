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

    private static readonly string Garment = ProjectGraph.GarmentId(new Resource(ColonyModelFixtures.Cloth), GearSlot.Unset);

    private static ColonyProjection Frame()
    {
        var projection = ColonyModelFixtures.Projection();
        var gear = projection.Facts.Gear.Value;
        return projection with
        {
            Facts = projection.Facts with
            {
                Gear = Known(gear with { Climate = ColonyModelFixtures.Climate() with { Temperatures = EquatableArray.From(new[] { -10.0, -8, -2, 5, 12, 18, 22, 21, 15, 8, 12, 12 }) }, Pawns = [gear.Pawns[0] with { LoadoutModel = new GearModel.Modeled(new GearLoadoutInput { Threat = ColonyModelFixtures.NoThreat, Options = [BillDuster()] }) }] }),
            },
            // 20 cells of 6 cloth each cover the duster's 80.
            Cells = [.. Enumerable.Range(0, 20).Select(x => new SiteCell { Cell = new Cell(x, 0), Zone = Known(true), ZoneId = Known("zone1"), Fertility = Known(0.8) })],
            CombinedFoodSupply = projection.CombinedFoodSupply with { Stocks = [projection.CombinedFoodSupply.Stocks[0] with { Nutrition = 10_000.0 }] },
        };
    }

    private static ForecastCoupledContext Context(HarmInputs harm)
    {
        var anna = ForecastValueTests.HarmPawn() with { Pawn = "Anna" };
        return ForecastValueTests.Context(anna, ForecastValueTests.HarmTables(), harm, new Dictionary<string, EquatableArray<GearOption>> { ["Anna"] = ForecastValueTests.Cloth });
    }

    [Fact]
    public void AFrameThatLeavesTheInputUnreadyReviewsToNoRunNoBasisAndNamesTheGaps()
    {
        var frame = ColonyFactsCoreTests.Projection();

        var review = RoundsColonyModel.Review(frame, Context(ForecastValueTests.RaidDay()), null);

        Assert.Null(review.Basis);
        Assert.Contains("colony_model_input_unready", review.Marginals.Findings);
        Assert.Empty(review.Marginals.Trajectory.Events);
        Assert.Contains(review.Unknowns, u => u.StartsWith("climate:", StringComparison.Ordinal));
    }

    [Fact]
    public void AReviewBuildsTheResearchGateTheCropChainAndTheCraftNodeAndPricesThemByTheHarmLedger()
    {
        var review = RoundsColonyModel.Review(Frame(), Context(ForecastValueTests.RaidDay()), null);
        var ids = review.Marginals.Trajectory.Events.Where(e => e.Kind is ModelEventKind.ProjectCompletion or ModelEventKind.Harvest).Select(e => e.Id).ToArray();

        Assert.Equal(["research:Devilstrand", "sow:zone1", "grow:zone1", "harvest:zone1", Garment], ids);
        // The raid wounds' treatments use up the four herbal medicine (#3634); the frame's pawns carry no tend quality curve, so the
        // tending that medicine buys is unpriced: a finding, not a zero (#3635).
        Assert.Contains(review.Marginals.Trajectory.Events, e => e.Kind == ModelEventKind.StockZero && e.Id == nameof(ModelStock.Medicine));
        Assert.DoesNotContain("medicine/MedicineHerbal", review.Marginals.PerUnit.Keys);
        Assert.Contains(review.Marginals.Findings, f => f.StartsWith(ColonyValue.MedicineSkillValueUnassessed, StringComparison.Ordinal));
        Assert.DoesNotContain(ColonyValue.ApparelHarmUnassessed, review.Marginals.Findings);
        Assert.True(review.Marginals.PerHour["research:Devilstrand"] > 0);
        Assert.True(review.Marginals.PerHour["sow:zone1"] > 0);
        Assert.True(review.Marginals.PerUnit["Devilstrand"] > 0);
    }

    [Fact]
    public void TheBiographysTendQualityCurveReachesTheReviewAndPricesTheMedicineItsRaidWoundsUseUp()
    {
        // #3638: the native biography's curves ride WorkPawn.SkillStatCurves into the review's pawns (#3635 left this unchecked).
        // Anna is now a Medicine-3 doctor with vanilla's tend quality curve, and the catalog reads herbal medicine's numbers.
        var frame = Frame();
        var tend = new SkillStatCurve(ColonyValue.TendQualityStat, "Medicine", EquatableArray.From(Enumerable.Range(0, 21).Select(l => new SkillStatLevel(l, 0.2 + (0.1 * l)))), "test");
        var anna = ((WorkPawnsRead.Read)frame.WorkPawns).Pawns[0];
        frame = frame with
        {
            Facts = frame.Facts with { MedicalReserve = frame.Facts.MedicalReserve with { Catalog = ItemFactsFixtureRules.CoreItemFacts() } },
            WorkPawns = new WorkPawnsRead.Read(EquatableArray.From(new[]
            {
                anna with
                {
                    Skills = EquatableArray.From(anna.Skills.Append(new WorkSkill { Name = "Medicine", Level = 3, Learning = SkillStockTests.Mechanics(3, 0) })),
                    SkillStatCurves = EquatableArray.From(new[] { tend }),
                },
            })),
        };

        var review = RoundsColonyModel.Review(frame, Context(ForecastValueTests.RaidDay()), null);

        Assert.DoesNotContain(review.Marginals.Findings, f => f.StartsWith(ColonyValue.MedicineSkillValueUnassessed, StringComparison.Ordinal));
        Assert.True(review.Marginals.PerUnit[ProjectGraph.MedicineKey(new Resource("MedicineHerbal"))] > 0, string.Join(',', review.Marginals.Findings));
    }

    [Fact]
    public void WhileTheExposureTableIsAPlaceholderTheNodeExistsHarmIsUnassessedAndTheInvestmentIsZero()
    {
        var review = RoundsColonyModel.Review(Frame(), Context(new HarmInputs()), null);

        Assert.True(review.Marginals.Trajectory.Events.Any(e => e.Id == Garment), string.Join(',', review.Marginals.Findings));
        Assert.Contains(ColonyValue.ApparelHarmUnassessed, review.Marginals.Findings);
        Assert.Equal(0, review.Marginals.PerHour["research:Devilstrand"]);
        Assert.Equal(0, review.Marginals.PerUnit["Devilstrand"]);
    }

    [Fact]
    public void EachReachableStuffAndSlotIsAGarmentNodeOfTheDefWithTheMostArmor()
    {
        var input = ColonyModelFixtures.ReadyOf(Frame());
        var better = BillDuster() with { ID = "bill:Apparel_Parka/DevilstrandCloth", Definition = new Resource("Apparel_Parka"), Sharp = 0.9 };
        var cotton = BillDuster() with { ID = "bill:Apparel_Duster/Cloth", Stuff = new Resource("Cloth"), Sharp = 0.99 };
        ProjectNode[] Garments(ProjectGraph graph) => [.. graph.Nodes.Where(n => n.Kind == ProjectKind.Craft)];

        // The parka beats the duster in the harvest's cloth, and the frame has no recipe for it; no cotton is grown or held.
        var graph = ProjectGraph.Standing(input, [BillDuster(), better, cotton], ColonyModelFixtures.NoThreat);
        Assert.Empty(Garments(graph));
        Assert.Contains("garment_recipe_unknown:Apparel_Parka", graph.Findings);
        Assert.Equal([Garment], Garments(ProjectGraph.Standing(input, [BillDuster()], ColonyModelFixtures.NoThreat)).Select(n => n.Id));
        Assert.Empty(Garments(ProjectGraph.Standing(input, [BillDuster() with { Sharp = 0, Blunt = 0 }], ColonyModelFixtures.NoThreat)));
    }
}
