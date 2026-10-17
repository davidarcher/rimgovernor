using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.NativeFacade;
using RimGovernor.Contracts.Wire;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The acceptance harness speaks the host's Contracts JSON through <see cref="WireCodec"/> (#3123). These tests pin the
/// codec to the Contracts records: a golden pair for the request that failed live, and a synthesized document for every
/// request and reply type of every wire method, with every case of every choice, that must survive host JSON ->
/// cases' flat view -> host JSON unchanged.
/// </summary>
public sealed class WireCodecTests
{
    public static TheoryData<string> Methods() => [.. NativeOps.All.Select(op => op.Name).Order(StringComparer.Ordinal)];

    [Fact]
    public void SetModeRequestIsTheHostsPolymorphicOperation()
    {
        var view = JsonNode.Parse("""{"setMode":{"identity":{"colonyId":"c","loadToken":"t","mapId":0},"expectedGeneration":196,"mode":"MODE_AUTO"}}""");

        var wire = WireCodec.EncodeRequest("authority_control", view);

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"operation":{"type":"setMode","value":{"identity":{"colonyId":"c","loadToken":"t","mapId":0},"expectedGeneration":196,"mode":"MODE_AUTO"}}}"""),
            JsonNode.Parse(wire)));
        var typed = WireJson.Deserialize<RimGovernor.Contracts.Wire.Authority.ControlRequest>(wire);
        Assert.Equal(196UL, typed.Operation!.SetMode!.ExpectedGeneration);
        Assert.Equal(RimGovernor.Contracts.Wire.Authority.Mode.Auto, typed.Operation.SetMode.Mode);
    }

    [Fact]
    public void ReplyOutcomeAndNestedStateAreFlattened()
    {
        var granted = WireCodec.DecodeReply("authority_control", """{"outcome":{"type":"granted","value":{"authority":{"mode":"MODE_AUTO"}}}}""");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"granted":{"authority":{"mode":"MODE_AUTO"}}}"""), granted));

        var status = WireCodec.DecodeReply("authority_read_status", """{"outcome":{"type":"status","value":{"state":{"type":"inactive","value":{"reason":"REVOCATION_REASON_MANUAL"}}}}}""");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"status":{"inactive":{"reason":"REVOCATION_REASON_MANUAL"}}}"""), status));
    }

    [Fact]
    public void HostEncodingOfAChoiceIsRefusedInARequest()
    {
        var host = JsonNode.Parse("""{"operation":{"type":"setMode","value":{}}}""");
        _ = Assert.Throws<AcceptanceException>(() => WireCodec.EncodeRequest("authority_control", host));
    }

    [Fact]
    public void TwoCasesOfOneChoiceAreRefused()
    {
        var view = JsonNode.Parse("""{"setMode":{},"revoke":{}}""");
        _ = Assert.Throws<AcceptanceException>(() => WireCodec.EncodeRequest("authority_control", view));
    }

    [Fact]
    public void AMethodWithoutAContractIsAHarnessError()
    {
        _ = Assert.Throws<AcceptanceException>(() => WireCodec.EncodeRequest("no_such_method", new JsonObject()));
    }

    [Theory]
    [MemberData(nameof(Methods))]
    public void EveryRequestAndReplyTypeRoundTrips(string method)
    {
        Assert.True(NativeOps.TryGet(method, out var op));
        var (request, reply) = (op.Request, op.Reply);
        foreach (var type in new[] { request, reply })
        {
            var variants = Choices(type, 0, []).DefaultIfEmpty(1).Max();
            for (var variant = 0; variant < variants; variant++)
            {
                var host = Fill(type, 0, variant)!;
                var text = host.ToJsonString();
                var typed = JsonSerializer.Deserialize(text, WireJsonContext.Default.GetTypeInfo(type)!)!;

                var view = WireCodec.ToView(host, type);
                Assert.Equal(variants > 1, !JsonNode.DeepEquals(host, view));
                Assert.True(JsonNode.DeepEquals(host, WireCodec.ToHost(view, type)), $"{method} {type.Name} variant {variant}: host -> view -> host changed the document");

                var viaView = JsonSerializer.Deserialize(WireCodec.ToHost(view, type)!.ToJsonString(), WireJsonContext.Default.GetTypeInfo(type)!)!;
                Assert.Equal(JsonSerializer.Serialize(typed, WireJsonContext.Default.GetTypeInfo(type)!), JsonSerializer.Serialize(viaView, WireJsonContext.Default.GetTypeInfo(type)!));
            }
        }
    }

    [Fact]
    public void CoupledObservationsRoundTripKnownZeroFalseAndUnknownWithNonemptyRecipeInputs()
    {
        var read = new RimGovernor.Contracts.Wire.Observations.CoupledForecastRead
        {
            Tick = 100,
            FoodUses = [new("pawn", "Rice", new(1, 1), RimGovernor.Brain.Domain.Fact.Known(false),
                RimGovernor.Brain.Domain.Fact.Known(0.0), default, 1, default)],
            Clothing = new() { Known = true, Options = [new("option", "pawn", "bench", "Tailor", "Shirt", "Shirt", "Cloth", "bill", "", new(1, 1),
                [new("stack", "Cloth", 10, new(2, 1), 20)], RimGovernor.Brain.Domain.Fact.Known(0.0), default,
                RimGovernor.Brain.Domain.Fact.Known(10.0), new(3, 1))], Retained = [new("retained", "Shirt", "Cloth", 1, default)] },
        };
        var decoded = WireJson.Deserialize<RimGovernor.Contracts.Wire.Observations.CoupledForecastRead>(WireJson.Serialize(read));
        Assert.Equal(RimGovernor.Brain.Domain.Fact.Known(false), decoded.FoodUses[0].Eligible);
        Assert.Equal(RimGovernor.Brain.Domain.Fact.Known(0.0), decoded.FoodUses[0].Nutrition);
        Assert.False(decoded.FoodUses[0].ChewTicks.IsKnown);
        Assert.Equal("stack", Assert.Single(Assert.Single(decoded.Clothing.Options).Inputs).Thing);
        Assert.False(Assert.Single(decoded.Clothing.Retained).CompletionTicks.IsKnown);
    }
    [Fact]
    public void HuntObservationsRoundTripWithUnknownRangeAndProducts()
    {
        var hunter = new RimGovernor.Contracts.Wire.Observations.HuntActorRead("pawn", "Colonist", new(3, 4), 30)
        {
            Weapon = new("Gun_BoltActionRifle", "", "Normal", 100), WeaponRange = RimGovernor.Brain.Domain.Fact.Known(30.0), Available = true,
            Skills = [new("Shooting", 8)], Traits = [new("Kind", 0)], Health = [new("Cut", "left arm", 0.5)], MissingParts = ["left eye"],
        };
        var prey = new RimGovernor.Contracts.Wire.Observations.HuntActorRead("boar", "Boar", new(9, 9), 3)
        {
            Sleeping = true, Herd = "wild-herd", Meat = RimGovernor.Brain.Domain.Fact.Known(0.0),
        };
        var read = new RimGovernor.Contracts.Wire.Observations.CoupledForecastRead
        {
            Hunt = new() { Known = true, Hunters = [hunter], Prey = [prey], Findings = ["f"],
                Scene = new("w", "build", "mods", "Clear", "Soil", 0, 0, 40, 40, 21.5, 1.0) { Ground = [new(1, 2, 3, 2, "Sand", "")], Obstacles = [new("Wall", "", 5, 5)] } },
        };
        var decoded = WireJson.Deserialize<RimGovernor.Contracts.Wire.Observations.CoupledForecastRead>(WireJson.Serialize(read)).Hunt;
        Assert.Equal(RimGovernor.Brain.Domain.Fact.Known(30.0), Assert.Single(decoded.Hunters).WeaponRange);
        Assert.Equal("left eye", Assert.Single(decoded.Hunters[0].MissingParts));
        Assert.Equal(RimGovernor.Brain.Domain.Fact.Known(0.0), Assert.Single(decoded.Prey).Meat);
        Assert.False(decoded.Prey[0].WeaponRange.IsKnown);
        Assert.Equal("Sand", Assert.Single(decoded.Scene!.Ground).Terrain);
    }
    [Fact]
    public void PlannedSitesRoundTripWithUnknownRouteIllegalAndUnknownVerdicts()
    {
        var request = new RimGovernor.Contracts.Wire.Observations.ConstructionActivityRequest(new() { ColonyId = "c", LoadToken = "l", MapId = 1 }, default)
        {
            Planned = [new("walls", "Wall", "WoodLog", RimGovernor.Brain.Domain.Rotation.East, [new(1, 1), new(2, 1)])],
        };
        var decodedRequest = WireJson.Deserialize<RimGovernor.Contracts.Wire.Observations.ConstructionActivityRequest>(WireJson.Serialize(request));
        Assert.Equal(request.Planned, decodedRequest.Planned);
        Assert.Equal(RimGovernor.Brain.Domain.Rotation.East, decodedRequest.Planned[0].Rotation);

        var read = new RimGovernor.Contracts.Wire.Observations.ConstructionActivityRead
        {
            Known = true,
            Planned = [
                new("walls", RimGovernor.Brain.Domain.Fact.Known(true)) { Options = [new("walls", "Wall", new(1, 1), "pawn", "WoodLog", "stack", new(5, 5), 10, 75, "ConstructFinishFrames",
                    default, default, RimGovernor.Brain.Domain.Fact.Known(270.0)) { SiteApproachTicks = RimGovernor.Brain.Domain.Fact.Known(0.0) }] },
                new("on-water", RimGovernor.Brain.Domain.Fact.Known(false)) { Rejections = [new(new(7, 5), "Terrain lacks affordance")],
                    Blockers = [new(new(7, 5), "Wall_1", "Wall", "Building")] },
                new("unresolved", default),
            ],
            Findings = ["planned_site_definition_unresolved"],
        };
        var decoded = WireJson.Deserialize<RimGovernor.Contracts.Wire.Observations.ConstructionActivityRead>(WireJson.Serialize(read));
        var walls = decoded.Planned[0];
        Assert.True(walls.CanPlace.Value);
        var row = Assert.Single(walls.Options);
        Assert.False(row.ApproachTicks.IsKnown);
        Assert.False(row.DeliveryTicks.IsKnown);
        Assert.Equal(RimGovernor.Brain.Domain.Fact.Known(0.0), row.SiteApproachTicks);
        Assert.Equal(270.0, row.FinishTicks.Value);
        var water = decoded.Planned[1];
        Assert.Equal(RimGovernor.Brain.Domain.Fact.Known(false), water.CanPlace);
        Assert.Equal("Terrain lacks affordance", Assert.Single(water.Rejections).Reason);
        Assert.Equal("Wall_1", Assert.Single(water.Blockers).Thing);
        Assert.False(decoded.Planned[2].CanPlace.IsKnown);
        Assert.Empty(decoded.Planned[2].Options);
        Assert.Equal(read.Planned, decoded.Planned);
    }

    /// <summary>Routes between planned sites (#3313): the probes and the route rows round trip, an unreached route stays unknown.</summary>
    [Fact]
    public void PlannedRoutesRoundTripWithStartProbesAndUnknownRoutes()
    {
        var request = new RimGovernor.Contracts.Wire.Observations.ConstructionActivityRequest(new() { ColonyId = "c", LoadToken = "l", MapId = 1 }, default)
        {
            Routes = [new(new(4, 4), new(6, 4), [new(6, 4)]), new(new(5, 5), new(5, 5), [new(5, 5), new(6, 5)]) { FromStart = true }],
        };
        var decodedRequest = WireJson.Deserialize<RimGovernor.Contracts.Wire.Observations.ConstructionActivityRequest>(WireJson.Serialize(request));
        Assert.Equal(request.Routes, decodedRequest.Routes);
        Assert.True(decodedRequest.Routes[1].FromStart);
        Assert.Equal(2, decodedRequest.Routes[1].Reach.Count);

        var read = new RimGovernor.Contracts.Wire.Observations.ConstructionActivityRead
        {
            Known = true,
            Routes = [new("pawn", new(4, 4), new(6, 4), RimGovernor.Brain.Domain.Fact.Known(4.0)),
                new("pawn", new(1, 1), new(5, 5), default) { FromStart = true }],
            Findings = ["planned_route_out_of_bounds"],
        };
        var decoded = WireJson.Deserialize<RimGovernor.Contracts.Wire.Observations.ConstructionActivityRead>(WireJson.Serialize(read));
        Assert.Equal(read.Routes, decoded.Routes);
        Assert.Equal(4.0, decoded.Routes[0].Ticks.Value);
        Assert.False(decoded.Routes[1].Ticks.IsKnown);
        Assert.True(decoded.Routes[1].FromStart);
    }

    [Fact]
    public void PlannedShelterThermalRoundTripsRoomsHeatersCellsAndUnknownSupply()
    {
        var request = new RimGovernor.Contracts.Wire.Observations.ConstructionActivityRequest(new() { ColonyId = "c", LoadToken = "l", MapId = 1 }, default)
        {
            Thermal = new()
            {
                Rooms = [new("3", new(10, 12), 4, 3)],
                Heaters = [new("planned/room3/build/Heater/11/13", "Heater", new(11, 13))],
            },
        };
        var decodedRequest = WireJson.Deserialize<RimGovernor.Contracts.Wire.Observations.ConstructionActivityRequest>(WireJson.Serialize(request));
        Assert.Equal(request.Thermal.Rooms, decodedRequest.Thermal!.Rooms);
        Assert.Equal(request.Thermal.Heaters, decodedRequest.Thermal.Heaters);
        Assert.Null(WireJson.Deserialize<RimGovernor.Contracts.Wire.Observations.ConstructionActivityRequest>(
            WireJson.Serialize(request with { Thermal = null })).Thermal);

        var read = new RimGovernor.Contracts.Wire.Observations.ConstructionActivityRead
        {
            Known = true,
            Thermal = new()
            {
                Cells = [new(new(9, 11), RimGovernor.Brain.Domain.Fact.Known(-12.5), "none", false, false, false, "", "", false, false, 0, false)],
                Sources = [new("planned/room3/build/Heater/11/13", new(11, 13), false, RimGovernor.Brain.Domain.Fact.Known(.35),
                    RimGovernor.Brain.Domain.Fact.Known(21.0), default) { AdditionalWatts = RimGovernor.Brain.Domain.Fact.Known(175.0) }],
                Workers = [new("pawn", RimGovernor.Brain.Domain.Fact.Known(38.2))],
                Outdoors = RimGovernor.Brain.Domain.Fact.Known(-12.5),
            },
        };
        var decoded = WireJson.Deserialize<RimGovernor.Contracts.Wire.Observations.ConstructionActivityRead>(WireJson.Serialize(read)).Thermal!;
        Assert.Equal(read.Thermal.Cells, decoded.Cells);
        var source = Assert.Single(decoded.Sources);
        Assert.Equal(read.Thermal.Sources[0], source);
        Assert.False(source.Complete);
        Assert.False(source.SupplyTicks.IsKnown);
        Assert.False(source.SurplusWatts.IsKnown);
        Assert.Equal("", source.PowerNetwork);
        Assert.Equal(read.Thermal.Workers, decoded.Workers);
        Assert.Empty(decoded.RoofRoutes);
        Assert.Null(WireJson.Deserialize<RimGovernor.Contracts.Wire.Observations.ConstructionActivityRead>(WireJson.Serialize(read with { Thermal = null })).Thermal);
    }

    [Fact]
    public void PlannedSiteClearingRoundTripsEachVerdictAndKeepsUnknownsUnknown()
    {
        var known = RimGovernor.Brain.Domain.Fact.Known(12.0);
        var read = new RimGovernor.Contracts.Wire.Observations.ConstructionActivityRead
        {
            Known = true,
            Planned = [
                new("clearable", RimGovernor.Brain.Domain.Fact.Known(false))
                {
                    Verdict = RimGovernor.Contracts.Wire.Observations.PlannedSiteVerdict.Feasible,
                    Rejections = [new(new(6, 7), "Space already occupied")],
                    Blockers = [
                        new(new(1, 7), "Plant_TreeOak_1", "Plant_TreeOak", "Plant") { Verdict = RimGovernor.Contracts.Wire.Observations.PlannedBlockerVerdict.Cleared, Clearing = "clearable/cut" },
                        new(new(0, 7), "Plant_Grass_2", "Plant_Grass", "Plant") { Verdict = RimGovernor.Contracts.Wire.Observations.PlannedBlockerVerdict.NoClearing },
                    ],
                    Clearing = [
                        new("clearable/cut", RimGovernor.Contracts.Wire.Observations.PlannedClearingKind.Cut, [new(1, 7)])
                        { Options = [new("clearable/cut", "Wall", new(1, 7), "pawn", "", "", new(1, 7), 0, 0, "PlantsCut", default, default, known) { SiteApproachTicks = known }] },
                        new("clearable/mine", RimGovernor.Contracts.Wire.Observations.PlannedClearingKind.Mine, [new(6, 7), new(7, 7)])
                        { Options = [new("clearable/mine", "Wall", new(6, 7), "slow", "", "", new(6, 7), 0, 0, "Miner", default, default, default) { SiteApproachTicks = known }] },
                        new("clearable/haul/Steel_3", RimGovernor.Contracts.Wire.Observations.PlannedClearingKind.Haul, [new(4, 7)])
                        { Options = [new("clearable/haul/Steel_3", "Wall", new(1, 9), "pawn", "Steel", "Steel_3", new(4, 7), 30, 75, "HaulGeneral", known, known, RimGovernor.Brain.Domain.Fact.Known(0.0))] },
                    ],
                },
                new("no-storage", RimGovernor.Brain.Domain.Fact.Known(true))
                { Blockers = [new(new(4, 7), "Steel_4", "Steel", "Item")] },
                new("on-wall", RimGovernor.Brain.Domain.Fact.Known(false))
                {
                    Verdict = RimGovernor.Contracts.Wire.Observations.PlannedSiteVerdict.Unestimable,
                    Rejections = [new(new(4, 5), "Identical thing exists")],
                    Blockers = [new(new(4, 5), "Wall_5", "Wall", "Building") { Verdict = RimGovernor.Contracts.Wire.Observations.PlannedBlockerVerdict.Unestimable }],
                },
                new("on-water", RimGovernor.Brain.Domain.Fact.Known(false))
                {
                    Verdict = RimGovernor.Contracts.Wire.Observations.PlannedSiteVerdict.Illegal,
                    Rejections = [new(new(7, 5), "Terrain lacks affordance") { Illegal = true }],
                    Blockers = [new(new(7, 5), "Hive_6", "Hive", "Building") { Verdict = RimGovernor.Contracts.Wire.Observations.PlannedBlockerVerdict.Illegal }],
                },
            ],
            Findings = ["planned_clearing_haul_destination_unknown"],
        };
        var json = WireJson.Serialize(read);
        Assert.Contains("PLANNED_SITE_VERDICT_FEASIBLE", json);
        Assert.Contains("PLANNED_CLEARING_KIND_HAUL", json);
        var decoded = WireJson.Deserialize<RimGovernor.Contracts.Wire.Observations.ConstructionActivityRead>(json);
        Assert.Equal(read.Planned, decoded.Planned);
        var clearable = decoded.Planned[0];
        Assert.Equal(RimGovernor.Contracts.Wire.Observations.PlannedSiteVerdict.Feasible, clearable.Verdict);
        Assert.False(Assert.Single(clearable.Rejections).Illegal);
        Assert.Equal("clearable/cut", clearable.Blockers[0].Clearing);
        Assert.Equal("", clearable.Blockers[1].Clearing);
        Assert.Equal(12.0, clearable.Clearing[0].Options[0].FinishTicks.Value);
        // A pawn without mining speed keeps an unknown finish, never zero.
        Assert.False(Assert.Single(clearable.Clearing[1].Options).FinishTicks.IsKnown);
        Assert.Equal(30, Assert.Single(clearable.Clearing[2].Options).Count);
        // No accepting storage: the haulable stays unknown, and so does the site.
        var unknown = decoded.Planned[1];
        Assert.Equal(RimGovernor.Contracts.Wire.Observations.PlannedSiteVerdict.Unspecified, unknown.Verdict);
        Assert.Equal(RimGovernor.Contracts.Wire.Observations.PlannedBlockerVerdict.Unspecified, Assert.Single(unknown.Blockers).Verdict);
        Assert.Empty(unknown.Clearing);
        Assert.Equal(RimGovernor.Contracts.Wire.Observations.PlannedSiteVerdict.Unestimable, decoded.Planned[2].Verdict);
        Assert.Equal(RimGovernor.Contracts.Wire.Observations.PlannedSiteVerdict.Illegal, decoded.Planned[3].Verdict);
        Assert.True(Assert.Single(decoded.Planned[3].Rejections).Illegal);
        Assert.Equal(RimGovernor.Contracts.Wire.Observations.PlannedBlockerVerdict.Illegal, Assert.Single(decoded.Planned[3].Blockers).Verdict);
    }

    [Fact]
    public void NeedTargetMaxPawnsRoundTripsKnownAndUnknownAsNumberOrNull()
    {
        var read = new RimGovernor.Contracts.Wire.Observations.NeedsForecastRead
        {
            Tick = 1,
            Pawns = [new() { Pawn = "p", Targets = [
                new("stack", "food", "Meal", 4, default, default, default, default) { MaxPawns = RimGovernor.Brain.Domain.Fact.Known(10) },
                new("pin", "joy", "", 1, default, default, default, default),
            ] }],
        };
        var json = WireJson.Serialize(read);
        var node = JsonNode.Parse(json)!;
        var targets = node["pawns"]![0]!["targets"]!.AsArray();
        Assert.Equal(10, targets[0]!["maxPawns"]!.GetValue<int>());
        Assert.Null(targets[1]!["maxPawns"]);
        var decoded = WireJson.Deserialize<RimGovernor.Contracts.Wire.Observations.NeedsForecastRead>(json).Pawns[0].Targets;
        Assert.Equal(RimGovernor.Brain.Domain.Fact.Known(10), decoded[0].MaxPawns);
        Assert.False(decoded[1].MaxPawns.IsKnown);
        targets[1]!["maxPawns"] = null;
        var explicitNull = WireJson.Deserialize<RimGovernor.Contracts.Wire.Observations.NeedsForecastRead>(node.ToJsonString()).Pawns[0].Targets;
        Assert.False(explicitNull[1].MaxPawns.IsKnown);
    }

    [Fact]
    public void NeedsRoofReadsRoundTripKnownAndUnknown()
    {
        var known = RimGovernor.Brain.Domain.Fact.Known(true);
        var read = new RimGovernor.Contracts.Wire.Observations.NeedsForecastRead
        {
            Tick = 1, WeatherThought = "SoakingWet", WeatherThoughtStages = 1, RainRate = RimGovernor.Brain.Domain.Fact.Known(.5),
            MapArea = RimGovernor.Brain.Domain.Fact.Known(62500), Deterioration = new(.5, .5, 0),
            Pawns = [new()
            {
                Pawn = "p", OutdoorsLevel = RimGovernor.Brain.Domain.Fact.Known(.7), OutdoorsDisabled = true, OwnedBed = "bed", Ascetic = true,
                ThoughtStages = [new("SleptInBedroom", "slept_in_bedroom", 2, 3, RimGovernor.Brain.Domain.Fact.Known(60000d), false),
                    new("NeedOutdoors", "need_outdoors", 4, -3, default, false)],
                Targets = [
                    new("stack", "food", "Meal", 4, default, default, default, default)
                        { Cell = new(known, default, known, known, known), Deterioration = new(50, RimGovernor.Brain.Domain.Fact.Known(10d), true, .5, false) },
                    new("bed", "rest", "", 1, default, default, default, default) { Bed = new("Bedroom", RimGovernor.Brain.Domain.Fact.Known(2), false) },
                    new("pin", "joy", "", 1, default, default, default, default),
                ],
            }],
        };
        Assert.Equal(read, WireJson.Deserialize<RimGovernor.Contracts.Wire.Observations.NeedsForecastRead>(WireJson.Serialize(read)));
        var unread = new RimGovernor.Contracts.Wire.Observations.NeedsForecastRead { Tick = 1 };
        var decoded = WireJson.Deserialize<RimGovernor.Contracts.Wire.Observations.NeedsForecastRead>(WireJson.Serialize(unread));
        Assert.Equal(unread, decoded);
        Assert.False(decoded.RainRate.IsKnown);
        Assert.Null(decoded.Deterioration);
    }

    [Fact]
    public void PlannedRoomPredictionsRoundTripRequestAndRead()
    {
        var request = new RimGovernor.Contracts.Wire.Observations.ConstructionActivityRequest(new() { ColonyId = "c", LoadToken = "l", MapId = 1 }, default)
        {
            RoomPredictions = [new("1", "WoodLog", [new(1, 1), new(1, 2)], [new(new(0, 1), "Wall", "WoodLog"), new(new(2, 1), "Door", "WoodLog")])],
        };
        Assert.Equal(request.RoomPredictions, WireJson.Deserialize<RimGovernor.Contracts.Wire.Observations.ConstructionActivityRequest>(WireJson.Serialize(request)).RoomPredictions);
        var read = new RimGovernor.Contracts.Wire.Observations.ConstructionActivityRead
        {
            RoomPredictions = [
                new("1", "WoodLog", "Bedroom", RimGovernor.Brain.Domain.Fact.Known(31.5), RimGovernor.Brain.Domain.Fact.Known(2), 2, 0, false)
                    { Beds = ["bed"], Benches = [new("stove", true)] },
                new("1", "Steel", "None", default, default, 2, 2, true),
            ],
        };
        Assert.Equal(read.RoomPredictions, WireJson.Deserialize<RimGovernor.Contracts.Wire.Observations.ConstructionActivityRead>(WireJson.Serialize(read)).RoomPredictions);
    }

    // The largest case count of any choice reachable from the type: how many documents cover every case.
    private static IEnumerable<int> Choices(Type type, int depth, HashSet<Type> seen)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (depth > 6 || !seen.Add(type))
        {
            yield break;
        }

        if (Element(type) is { } element)
        {
            foreach (var count in Choices(element, depth + 1, seen))
            {
                yield return count;
            }

            yield break;
        }

        var info = WireJsonContext.Default.GetTypeInfo(type);
        if (info?.Kind != JsonTypeInfoKind.Object)
        {
            yield break;
        }

        foreach (var property in info.Properties)
        {
            if (WireJsonContext.Default.GetTypeInfo(property.PropertyType)?.PolymorphismOptions is { } options)
            {
                yield return options.DerivedTypes.Count;
                foreach (var derived in options.DerivedTypes)
                {
                    foreach (var count in Choices(derived.DerivedType.GetProperty("Value")!.PropertyType, depth + 1, seen))
                    {
                        yield return count;
                    }
                }
            }
            else
            {
                foreach (var count in Choices(property.PropertyType, depth + 1, seen))
                {
                    yield return count;
                }
            }
        }
    }

    private static Type? Element(Type type)
    {
        if (type == typeof(string) || type == typeof(byte[]))
        {
            return null;
        }

        return type.GetInterfaces().Append(type)
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(i => i.GetGenericArguments()[0])
            .FirstOrDefault();
    }

    // A host-JSON document for the type with every optional member present and each choice on case (variant % cases).
    private static JsonNode? Fill(Type type, int depth, int variant)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(RimGovernor.Brain.Domain.Fact<>))
        {
            return Fill(type.GetGenericArguments()[0], depth, variant);
        }

        if (type == typeof(string))
        {
            return "x";
        }

        if (type == typeof(bool))
        {
            return true;
        }

        if (type == typeof(byte[]))
        {
            return "AQID";
        }

        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            return JsonSerializer.SerializeToNode(values.GetValue(Math.Min(1, values.Length - 1)), type, WireJsonContext.Default.Options);
        }

        if (type == typeof(double) || type == typeof(float))
        {
            return 1.5;
        }

        if (type.IsPrimitive)
        {
            return 1;
        }

        if (type.GetInterfaces().Append(type).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)) is { } map)
        {
            var value = depth > 6 ? null : Fill(map.GetGenericArguments()[1], depth + 1, variant);
            return value is null ? new JsonObject() : new JsonObject { ["k"] = value };
        }

        if (Element(type) is { } element)
        {
            var item = depth > 6 ? null : Fill(element, depth + 1, variant);
            return item is null ? new JsonArray() : new JsonArray(item);
        }

        var info = WireJsonContext.Default.GetTypeInfo(type) ?? throw new InvalidOperationException($"{type} is not registered");
        // Global fact converters also encode nullable values; their CLR type metadata has no object properties.
        if (info.Converter.GetType().IsGenericType && info.Converter.GetType().GetGenericTypeDefinition() == typeof(FactJsonConverter<>))
            return Fill(type.GetGenericArguments()[0], depth, variant);
        var obj = new JsonObject();
        foreach (var property in info.Properties)
        {
            if (depth > 6 && !property.IsRequired)
            {
                continue;
            }


            if (WireJsonContext.Default.GetTypeInfo(property.PropertyType)?.PolymorphismOptions is { } options)
            {
                var derived = options.DerivedTypes[variant % options.DerivedTypes.Count];
                obj[property.Name] = new JsonObject
                {
                    [options.TypeDiscriminatorPropertyName!] = (string)derived.TypeDiscriminator!,
                    ["value"] = Fill(derived.DerivedType.GetProperty("Value")!.PropertyType, depth + 1, variant),
                };
            }
            else
            {
                // Custom observation converters encode domain facts as nullable wire values, not CLR getter objects.
                obj[property.Name] = property.CustomConverter switch
                {
                    ObservedNumberJsonConverter => JsonValue.Create(1.5),
                    ObservedMemoriesJsonConverter => new JsonArray(),
                    ObservedConditionalThoughtsJsonConverter => new JsonArray(),
                    _ => Fill(property.PropertyType, depth + 1, variant),
                };
            }
        }

        return obj;
    }
}
