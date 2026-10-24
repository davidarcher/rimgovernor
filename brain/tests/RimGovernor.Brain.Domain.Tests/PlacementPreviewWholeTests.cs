using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using Xunit;
using C = RimGovernor.Contracts.Wire.Common;
using Pl = RimGovernor.Contracts.Wire.Placement;

namespace RimGovernor.Brain.Domain.Tests;

// #3922: a placement preview is read whole. The native answers every member of a rotation, or fails the candidate;
// the watch-cell and wind-catch facts are named states, never an absent value.
public sealed class PlacementPreviewWholeTests
{
    private static readonly C.ObservationContext Context = new() { NativeGeneration = new C.ObservedGeneration.UnclaimedCase(), Identity = new C.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 10 };

    private static Pl.PlacementRotation Rotation(Pl.WatchAccess watch, BuildingWind wind) => new()
    {
        Accepted = true,
        Reason = "",
        OccupiedCells = [new C.Cell { X = 3, Z = 4 }],
        BlockingThings = [],
        WatchCells = watch,
        InteractionCells = [new C.Cell { X = 3, Z = 5 }],
        Wind = wind,
    };

    private static Pl.PlacementEvaluated Evaluated(Pl.WatchAccess watch, BuildingWind wind) => new()
    {
        CanPlace = true,
        MadeFromStuff = false,
        ResearchFinished = true,
        BuildableByPlayer = true,
        CostList = [new Pl.PlacementCost { DefName = "Steel", Count = 20 }],
        Materials = new Pl.PlacementMaterials { Rows = [new Pl.PlacementMaterialStock { DefName = "Steel", Available = 0 }] },
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
        foreach (var watch in new[] { Pl.WatchAccess.NotWatched, Pl.WatchAccess.Reachable, Pl.WatchAccess.Unreachable })
        {
            foreach (BuildingWind wind in new BuildingWind[] { new BuildingWind.NotWindTurbine(), new BuildingWind.WindTurbine { BlockedCells = 0 }, new BuildingWind.WindTurbine { BlockedCells = 7 } })
            {
                var batch = new Pl.PlacementBatch { Context = Context, Results = [new Pl.CandidateReply { Outcome = Evaluated(watch, wind) }] };
                var json = WireJson.Serialize(batch);
                var back = WireJson.Deserialize<Pl.PlacementBatch>(json);

                Assert.Equal(json, WireJson.Serialize(back));
                Assert.Equal(batch, back);
            }
        }
    }

    [Fact]
    public void TheWatchStatesTravelByWireName()
    {
        var json = WireJson.Serialize(Rotation(Pl.WatchAccess.Unreachable, new BuildingWind.NotWindTurbine()));

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
        var node = JsonNode.Parse(WireJson.Serialize(Rotation(Pl.WatchAccess.Reachable, new BuildingWind.NotWindTurbine())))!.AsObject();
        Assert.True(node.Remove(member));

        Assert.ThrowsAny<JsonException>(() => WireJson.Deserialize<Pl.PlacementRotation>(node.ToJsonString()));
    }

    [Theory]
    [InlineData("stuff")]
    [InlineData("ignoreNaturalRock")]
    [InlineData("rotation")]
    public void ACandidateMissingAMemberDoesNotDecode(string member)
    {
        var candidate = new Pl.PlacementCandidate { DefName = "Wall", X = 1, Z = 2, Rotation = Pl.Rotation.North, Stuff = "", IgnoreNaturalRock = false };
        var node = JsonNode.Parse(WireJson.Serialize(candidate))!.AsObject();
        Assert.True(node.Remove(member));

        Assert.ThrowsAny<JsonException>(() => WireJson.Deserialize<Pl.PlacementCandidate>(node.ToJsonString()));
    }

    [Fact]
    public void AnUnspecifiedRotationDoesNotDecode()
    {
        var candidate = new Pl.PlacementCandidate { DefName = "Wall", X = 1, Z = 2, Rotation = Pl.Rotation.North, Stuff = "", IgnoreNaturalRock = false };
        var json = WireJson.Serialize(candidate).Replace("ROTATION_NORTH", "ROTATION_UNSPECIFIED");

        Assert.ThrowsAny<JsonException>(() => WireJson.Deserialize<Pl.PlacementCandidate>(json));
    }

    [Theory]
    [InlineData(Pl.WatchAccess.NotWatched, false)]
    [InlineData(Pl.WatchAccess.Reachable, false)]
    [InlineData(Pl.WatchAccess.Unreachable, true)]
    public void OnlyAnUnreachableWatchBuildingIsRefused(Pl.WatchAccess watch, bool unreachable)
    {
        var (action, snapshot) = Subject("HorseshoesPin");
        var row = FacadeNativeClient.PlacementRequests.BuildingPreviewRow(action, snapshot, new Tick(10), Evaluated(watch, new BuildingWind.NotWindTurbine()));

        Assert.Equal(watch, row.Preview.WatchCells);
        Assert.Equal(unreachable, PreviewRules.WatchUnreachable(row.Preview));
    }

    [Fact]
    public void OnlyAClearWindTurbineIsAWindSite()
    {
        var (action, snapshot) = Subject("WindTurbine");

        var clear = FacadeNativeClient.PlacementRequests.BuildingPreviewRow(action, snapshot, new Tick(10), Evaluated(Pl.WatchAccess.NotWatched, new BuildingWind.WindTurbine { BlockedCells = 0 }));
        var blocked = FacadeNativeClient.PlacementRequests.BuildingPreviewRow(action, snapshot, new Tick(10), Evaluated(Pl.WatchAccess.NotWatched, new BuildingWind.WindTurbine { BlockedCells = 5 }));
        var other = FacadeNativeClient.PlacementRequests.BuildingPreviewRow(action, snapshot, new Tick(10), Evaluated(Pl.WatchAccess.NotWatched, new BuildingWind.NotWindTurbine()));

        Assert.True(PreviewRules.ClearWindTurbine(clear.Preview));
        Assert.False(PreviewRules.ClearWindTurbine(blocked.Preview));
        Assert.False(PreviewRules.ClearWindTurbine(other.Preview));
    }

    [Fact]
    public void ABuildingRowCarriesTheWholeEvaluation()
    {
        var (action, snapshot) = Subject("Wall");
        var row = FacadeNativeClient.PlacementRequests.BuildingPreviewRow(action, snapshot, new Tick(10), Evaluated(Pl.WatchAccess.NotWatched, new BuildingWind.NotWindTurbine()));

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

        Assert.True(preview.CanPlace);
        Assert.True(preview.SafeToPlace);
        Assert.False(preview.MadeFromStuff);
        Assert.Equal(Pl.WatchAccess.NotWatched, preview.WatchCells);
        Assert.IsType<BuildingWind.NotWindTurbine>(preview.Wind);
        Assert.Empty(preview.Costs);
        Assert.Empty(preview.Blockers);
        Assert.False(PreviewRules.WatchUnreachable(preview));
        Assert.False(PreviewRules.ClearWindTurbine(preview));
    }
}
