using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Domain;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class QuestSiteExitObservationTests
{
    private static RimGovernor.Brain.GameFacts.Observations.WorldProgressionSnapshot Read(RimGovernor.Brain.GameFacts.Observations.QuestSiteExtraction extraction) => new()
    {
        Context = new() { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(), Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 4 }, Tick = 100 },
        Maps = [], Caravans = [], Assemblies = [], Quests = [],
        Sites = [new RimGovernor.Brain.GameFacts.Observations.WorldSite
        {
            Id = "Site_3",
            State = RimGovernor.Brain.GameFacts.Observations.WorldSiteState.Spawned,
            Place = new RimGovernor.Brain.GameFacts.Observations.SitePlace.Loaded(12, 0, new RimGovernor.Brain.GameFacts.Observations.SiteMap
            {
                Id = new MapId(4),
                Threat = new RimGovernor.Brain.GameFacts.Observations.SiteThreat.Clear(),
                Extraction = extraction,
                MiningTargets = [],
            }),
            QuestIds = [],
            Route = new RimGovernor.Brain.GameFacts.Observations.SiteRoute.NoCrew(),
            Security = new RimGovernor.Brain.GameFacts.Observations.SiteThreatBudget.Bounded(0, 0),
        }],
    };

    [Fact]
    public void CombatUsesTheActualLoadedSitesExtractionCrew()
    {
        var native = new RimGovernor.Brain.GameFacts.Observations.ExtractionCrew
        {
            CanReform = false,
            Crew = [new PawnId("Human1")],
            CarryCapacity = 35,
            CarriedMass = 9,
            InventoryFoodDays = 2,
            CrewNutritionPerDay = 1.6,
            Cargo = [],
            HomeRoutes = [],
            ExitCells = [new Cell(0, 5)],
        };

        Assert.Same(native, QuestReads.CombatSiteExitOf(Read(new RimGovernor.Brain.GameFacts.Observations.QuestSiteExtraction.Crew(native)), new MapId(4)));
    }

    [Fact]
    public void NoAbleCrewOrNoLoadedSiteHasNoCombatExtraction()
    {
        var read = Read(new RimGovernor.Brain.GameFacts.Observations.QuestSiteExtraction.NoCrew());

        Assert.Null(QuestReads.CombatSiteExitOf(read, new MapId(4)));
        Assert.Null(QuestReads.CombatSiteExitOf(read, new MapId(5)));
        Assert.Null(QuestReads.CombatSiteExitOf(null, new MapId(4)));
    }
}
