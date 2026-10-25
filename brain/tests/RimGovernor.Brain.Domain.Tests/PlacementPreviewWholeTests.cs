using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3922: a placement preview is read whole. The native answers every member of a rotation, or fails the candidate;
// the watch-cell and wind-catch facts are named states, never an absent value.
public sealed class PlacementPreviewWholeTests
{
    [Fact]
    public void NativePreviewKeepsItsActualEvaluationOrientationAndBlockers()
    {
        var blocker = new RimGovernor.Brain.GameFacts.Placement.PlacementBlocker
        {
            DefName = "Wall", Category = "Building", IsBlueprint = false, IsFrame = false,
            WouldBeWiped = true, FrameWouldBeCancelled = false,
        };
        var wind = new BuildingWind.WindTurbine { BlockedCells = 5000 };
        var evaluation = Evaluated(RimGovernor.Brain.GameFacts.Placement.WatchAccess.Reachable, wind);
        evaluation = evaluation with { Rotations = [evaluation.Rotations[0] with { BlockingThings = [blocker] }] };
        var (action, snapshot) = Subject("Wall");
        var preview = GameClient.PlacementRequests.BuildingPreviewRow(action, snapshot, new(10), evaluation).Preview;
        var native = Assert.IsType<PreviewSource.Native>(preview.Source);
        Assert.Same(evaluation, native.Observed);
        Assert.Same(evaluation.Rotations[0], native.Orientation);
        Assert.Same(blocker, Assert.Single(preview.Blockers));
        Assert.Same(wind, preview.Wind);
        Assert.False(preview.SafeToPlace);
        Assert.False(PreviewRules.ClearWindTurbine(preview));
        Assert.Equal(RimGovernor.Brain.GameFacts.Placement.WatchAccess.Reachable, preview.WatchCells);
    }

    private static readonly RimGovernor.Brain.GameFacts.Common.ObservationContext Context = new() { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(), Identity = new RimGovernor.Brain.GameFacts.Common.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 10 };

    private static RimGovernor.Brain.GameFacts.Placement.PlacementRotation Rotation(RimGovernor.Brain.GameFacts.Placement.WatchAccess watch, BuildingWind wind) => new()
    {
        Accepted = true,
        Reason = "",
        OccupiedCells = [new RimGovernor.Brain.Domain.Cell { X = 3, Z = 4 }],
        BlockingThings = [],
        WatchCells = watch,
        InteractionCells = [new RimGovernor.Brain.Domain.Cell { X = 3, Z = 5 }],
        Wind = wind,
    };

    private static RimGovernor.Brain.GameFacts.Placement.PlacementEvaluated Evaluated(RimGovernor.Brain.GameFacts.Placement.WatchAccess watch, BuildingWind wind) => new()
    {
        CanPlace = true,
        MadeFromStuff = false,
        ResearchFinished = true,
        BuildableByPlayer = true,
        CostList = [new RimGovernor.Brain.GameFacts.Placement.PlacementCost { DefName = "Steel", Count = 20 }],
        Materials = new RimGovernor.Brain.GameFacts.Placement.PlacementMaterials { Rows = [new RimGovernor.Brain.GameFacts.Placement.PlacementMaterialStock { DefName = "Steel", Available = 0 }] },
        Rotations = [Rotation(watch, wind)],
    };

    private static (DomainAction Action, GenerationSnapshot Snapshot) Subject(string definition)
    {
        var snapshot = new GenerationSnapshot { Plan = new PlanId("plan"), Revision = new PlanRevision(1) };
        var action = DomainAction.NewBuildingAction(new ActionId("plan-0"), new Building(definition, new Cell(3, 4), Domain.Rotation.North, ""), ConstructionTier.Sustain);
        return (action, snapshot);
    }

    [Fact]
    public void AnEvaluatedCandidateRoundTripsEveryWatchAndWindState()
    {
        foreach (var watch in new[] { RimGovernor.Brain.GameFacts.Placement.WatchAccess.NotWatched, RimGovernor.Brain.GameFacts.Placement.WatchAccess.Reachable, RimGovernor.Brain.GameFacts.Placement.WatchAccess.Unreachable })
        {
            foreach (BuildingWind wind in new BuildingWind[] { new BuildingWind.NotWindTurbine(), new BuildingWind.WindTurbine { BlockedCells = 0 }, new BuildingWind.WindTurbine { BlockedCells = 7 } })
            {
                var batch = new RimGovernor.Brain.GameFacts.Placement.PlacementBatch { Context = Context, Results = [new RimGovernor.Brain.GameFacts.Placement.CandidateReply { Outcome = Evaluated(watch, wind) }] };
                var json = OrdersJson.Serialize(batch);
                var back = OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Placement.PlacementBatch>(json);

                Assert.Equal(json, OrdersJson.Serialize(back));
                Assert.Equal(batch, back);
            }
        }
    }

    [Fact]
    public void TheWatchStatesTravelByWireName()
    {
        var json = OrdersJson.Serialize(Rotation(RimGovernor.Brain.GameFacts.Placement.WatchAccess.Unreachable, new BuildingWind.NotWindTurbine()));

        Assert.Contains("\"watchCells\":\"unreachable\"", json);
        Assert.Contains("\"type\":\"not_wind_turbine\"", json);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("reason")]
    [InlineData("occupiedCells")]
    [InlineData("blockingThings")]
    [InlineData("watchCells")]
    [InlineData("interactionCells")]
    [InlineData("wind")]
    public void ARotationMissingAMemberDoesNotDecode(string member)
    {
        var node = JsonNode.Parse(OrdersJson.Serialize(Rotation(RimGovernor.Brain.GameFacts.Placement.WatchAccess.Reachable, new BuildingWind.NotWindTurbine())))!.AsObject();
        Assert.True(node.Remove(member));

        Assert.ThrowsAny<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Placement.PlacementRotation>(node.ToJsonString()));
    }

    [Theory]
    [InlineData("stuff")]
    [InlineData("ignoreNaturalRock")]
    [InlineData("rotation")]
    public void ACandidateMissingAMemberDoesNotDecode(string member)
    {
        var candidate = new RimGovernor.Brain.GameOrders.Placement.PlacementCandidate { DefName = "Wall", X = 1, Z = 2, Rotation = RimGovernor.Brain.GameFacts.Placement.Rotation.North, Stuff = "", IgnoreNaturalRock = false };
        var node = JsonNode.Parse(OrdersJson.Serialize(candidate))!.AsObject();
        Assert.True(node.Remove(member));

        Assert.ThrowsAny<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Placement.PlacementCandidate>(node.ToJsonString()));
    }

    [Fact]
    public void AnUnspecifiedRotationDoesNotDecode()
    {
        var candidate = new RimGovernor.Brain.GameOrders.Placement.PlacementCandidate { DefName = "Wall", X = 1, Z = 2, Rotation = RimGovernor.Brain.GameFacts.Placement.Rotation.North, Stuff = "", IgnoreNaturalRock = false };
        var json = OrdersJson.Serialize(candidate).Replace("ROTATION_NORTH", "ROTATION_UNSPECIFIED");

        Assert.ThrowsAny<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Placement.PlacementCandidate>(json));
    }

    [Theory]
    [InlineData(RimGovernor.Brain.GameFacts.Placement.WatchAccess.NotWatched, false)]
    [InlineData(RimGovernor.Brain.GameFacts.Placement.WatchAccess.Reachable, false)]
    [InlineData(RimGovernor.Brain.GameFacts.Placement.WatchAccess.Unreachable, true)]
    public void OnlyAnUnreachableWatchBuildingIsRefused(RimGovernor.Brain.GameFacts.Placement.WatchAccess watch, bool unreachable)
    {
        var (action, snapshot) = Subject("HorseshoesPin");
        var row = GameClient.PlacementRequests.BuildingPreviewRow(action, snapshot, new Tick(10), Evaluated(watch, new BuildingWind.NotWindTurbine()));

        Assert.Equal(watch, row.Preview.WatchCells);
        Assert.Equal(unreachable, PreviewRules.WatchUnreachable(row.Preview));
    }

    [Fact]
    public void OnlyAClearWindTurbineIsAWindSite()
    {
        var (action, snapshot) = Subject("WindTurbine");

        var clear = GameClient.PlacementRequests.BuildingPreviewRow(action, snapshot, new Tick(10), Evaluated(RimGovernor.Brain.GameFacts.Placement.WatchAccess.NotWatched, new BuildingWind.WindTurbine { BlockedCells = 0 }));
        var blocked = GameClient.PlacementRequests.BuildingPreviewRow(action, snapshot, new Tick(10), Evaluated(RimGovernor.Brain.GameFacts.Placement.WatchAccess.NotWatched, new BuildingWind.WindTurbine { BlockedCells = 5 }));
        var other = GameClient.PlacementRequests.BuildingPreviewRow(action, snapshot, new Tick(10), Evaluated(RimGovernor.Brain.GameFacts.Placement.WatchAccess.NotWatched, new BuildingWind.NotWindTurbine()));

        Assert.True(PreviewRules.ClearWindTurbine(clear.Preview));
        Assert.False(PreviewRules.ClearWindTurbine(blocked.Preview));
        Assert.False(PreviewRules.ClearWindTurbine(other.Preview));
    }

    [Fact]
    public void ABuildingRowCarriesTheWholeEvaluation()
    {
        var (action, snapshot) = Subject("Wall");
        var row = GameClient.PlacementRequests.BuildingPreviewRow(action, snapshot, new Tick(10), Evaluated(RimGovernor.Brain.GameFacts.Placement.WatchAccess.NotWatched, new BuildingWind.NotWindTurbine()));

        Assert.True(row.Preview.CanPlace);
        Assert.True(row.Preview.SafeToPlace);
        Assert.False(row.Preview.MadeFromStuff);
        Assert.Equal([new Cell(3, 4), new Cell(3, 5)], row.Preview.Footprint.ToArray());
        Assert.Equal(20, Assert.Single(row.Preview.Costs).Count);
        Assert.Empty(row.Preview.Blockers);
    }

    [Fact]
    public void AZoneStandInIsWhole()
    {
        var (_, snapshot) = Subject("Wall");
        var action = DomainAction.NewZoneCreateAction(new ActionId("plan-z"), new ZoneCreate(Domain.ZoneKind.Growing, "Rice", [new Cell(1, 1)]));
        var preview = PreviewRules.Zone(action, snapshot, new EquatableArray<Cell>([new Cell(1, 1)]));

        Assert.IsType<PreviewSource.Zone>(preview.Source);
        Assert.True(preview.CanPlace);
        Assert.True(preview.SafeToPlace);
        Assert.False(preview.MadeFromStuff);
        Assert.Equal(RimGovernor.Brain.GameFacts.Placement.WatchAccess.NotWatched, preview.WatchCells);
        Assert.IsType<BuildingWind.NotWindTurbine>(preview.Wind);
        Assert.Empty(preview.Costs);
        Assert.Empty(preview.Blockers);
        Assert.False(PreviewRules.WatchUnreachable(preview));
        Assert.False(PreviewRules.ClearWindTurbine(preview));
    }
}
