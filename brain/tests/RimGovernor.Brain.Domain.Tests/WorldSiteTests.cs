using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A world site row is total (#3861): its place, route, threat budget and loaded-map threat are named unions, the
/// extraction a crew or none. On the wire it round-trips through <c>OrdersJsonContext</c> exactly.
/// </summary>
public sealed class WorldSiteTests
{
    [Fact]
    public void JsonRoundTripIsExact()
    {
        var crew = new PawnId("Thing_Human1");
        var loaded = new WorldSite
        {
            Id = "Site_3",
            State = WorldSiteState.Spawned,
            Place = new SitePlace.Loaded(12, 0, new SiteMap
            {
                Id = new MapId(4),
                Threat = new SiteThreat.Trapped(),
                Extraction = new QuestSiteExtraction.Crew(new ExtractionCrew
                {
                    CanReform = true,
                    Crew = [crew],
                    CarryCapacity = 75,
                    CarriedMass = 10.5,
                    InventoryFoodDays = 2,
                    CrewNutritionPerDay = 1.6,
                    Cargo = [new QuestSiteCargo { Id = "Thing_Gold7", Def = "Gold", Count = 30, UnitMass = 0.008, MarketValue = 10, Nutrition = 0, Held = false }],
                    HomeRoutes =
                    [
                        new QuestSiteHomeRoute { Map = new MapId(0), Tile = 40, Travel = new SiteTravel.Reachable(60000) },
                        new QuestSiteHomeRoute { Map = new MapId(1), Tile = 41, Travel = new SiteTravel.Unreachable() },
                    ],
                    ExitCells = [new Cell(0, 5)],
                }),
                MiningTargets = [new QuestSiteMiningTarget { Id = "Thing_MineableGold8", Def = "MineableGold", Cell = new Cell(9, 9), Designated = true }],
            }),
            QuestIds = [new QuestId("7")],
            Route = new SiteRoute.Reachable(30000, [crew]),
            Security = new SiteThreatBudget.Bounded(350, 0),
        };
        var pending = new WorldSite
        {
            Id = "PeaceTalks_5",
            State = WorldSiteState.Pending,
            Place = new SitePlace.OnTile(20, 0),
            QuestIds = [],
            Route = new SiteRoute.NoCrew(),
            Security = new SiteThreatBudget.NotASite(),
            PeaceTalks = new QuestPeaceTalks { WorstGoodwillLoss = 30, BestGoodwillGain = 15, IdeologyActive = false },
        };

        var json = OrdersJson.Serialize(loaded);
        var other = OrdersJson.Serialize(pending);

        Assert.Equal(
            """{"id":"Site_3","state":"spawned","place":{"type":"loaded","tile":12,"layer":0,"map":{"id":4,"threat":{"type":"trapped"},"extraction":{"type":"crew","value":{"canReform":true,"crew":["Thing_Human1"],"carryCapacity":75,"carriedMass":10.5,"inventoryFoodDays":2,"crewNutritionPerDay":1.6,"cargo":[{"id":"Thing_Gold7","def":"Gold","count":30,"unitMass":0.008,"marketValue":10,"nutrition":0,"held":false}],"homeRoutes":[{"map":0,"tile":40,"travel":{"type":"reachable","travelTicks":60000}},{"map":1,"tile":41,"travel":{"type":"unreachable"}}],"exitCells":[{"x":0,"z":5}]}},"miningTargets":[{"id":"Thing_MineableGold8","def":"MineableGold","cell":{"x":9,"z":9},"designated":true}]}},"questIds":["7"],"route":{"type":"reachable","travelTicks":30000,"crew":["Thing_Human1"]},"security":{"type":"bounded","initialPoints":350,"pendingRaidPoints":0},"peaceTalks":null}""",
            json);
        Assert.Equal(
            """{"id":"PeaceTalks_5","state":"pending","place":{"type":"on_tile","tile":20,"layer":0},"questIds":[],"route":{"type":"no_crew"},"security":{"type":"not_a_site"},"peaceTalks":{"worstGoodwillLoss":30,"bestGoodwillGain":15,"ideologyActive":false}}""",
            other);
        Assert.Equal(loaded, OrdersJson.Deserialize<WorldSite>(json));
        Assert.Equal(pending, OrdersJson.Deserialize<WorldSite>(other));
    }
}
