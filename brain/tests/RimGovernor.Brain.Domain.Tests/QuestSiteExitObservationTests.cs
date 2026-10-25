using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class QuestSiteExitObservationTests
{
    private static RimGovernor.Contracts.Wire.Observations.WorldProgressionSnapshot Read(O.QuestSiteExtraction extraction) => new()
    {
        Context = new() { NativeGeneration = new RimGovernor.Contracts.Wire.Common.ObservedGeneration.UnclaimedCase(), Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 4 }, Tick = 100 },
        Maps = [], Caravans = [], Assemblies = [], Quests = [],
        Sites = [new O.WorldSite
        {
            Id = "Site_3",
            State = O.WorldSiteState.Spawned,
            Place = new O.SitePlace.Loaded(12, 0, new O.SiteMap
            {
                Id = new MapId(4),
                Threat = new O.SiteThreat.Clear(),
                Extraction = extraction,
                MiningTargets = [],
            }),
            QuestIds = [],
            Route = new O.SiteRoute.NoCrew(),
            Security = new O.SiteThreatBudget.Bounded(0, 0),
        }],
    };

    [Fact]
    public void CombatUsesTheActualLoadedSitesExtractionCrew()
    {
        var native = new O.ExtractionCrew
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

        Assert.Same(native, QuestReads.CombatSiteExitOf(Read(new O.QuestSiteExtraction.Crew(native)), new MapId(4)));
    }

    [Fact]
    public void NoAbleCrewOrNoLoadedSiteHasNoCombatExtraction()
    {
        var read = Read(new O.QuestSiteExtraction.NoCrew());

        Assert.Null(QuestReads.CombatSiteExitOf(read, new MapId(4)));
        Assert.Null(QuestReads.CombatSiteExitOf(read, new MapId(5)));
        Assert.Null(QuestReads.CombatSiteExitOf(null, new MapId(4)));
    }
}
