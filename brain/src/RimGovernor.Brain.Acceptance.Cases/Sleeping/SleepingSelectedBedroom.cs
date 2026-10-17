using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Store.Codec;
using Wired = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>sleeping/selected-bedroom</c> (#3278, #3340): the starter-site layout plan, bedroom wing included, is injected into
/// the save, so the bedroom is an unstarted planned room from the first review. The watch ends at the first bedroom
/// shell placement; the case then asserts against native buildings that the placement stands on exactly the sites the
/// latest coupled selection round before it completes (#3342), in the selected ring stuff, and on no other site of
/// that room. Construction is not waited for.
/// </summary>
internal static class SleepingSelectedBedroom
{
    /// <summary>The stem of a bedroom room's reconcile methods (BedroomReconcileName): the room id follows.</summary>
    private const string ShellRoom = SleepingBedrooms.BedroomShellPrefix + "room";

    // A plan stage past these means the placement was handed to native.
    private static readonly string[] Handed = [Wire.Name(Stage.Dispatched), Wire.Name(Stage.AwaitingObservation), Wire.Name(Stage.Completed)];

    internal static AcceptanceCase Case()
    {
        StarterSitePlan? site = null;
        return new()
        {
            Name = "sleeping/selected-bedroom",
            Scope = "Issue #3278/#3340: on the tribal " + SustainedFoodCase.BaselineSave + " colony at Masonry with a fixture hut bedding everyone "
                + "and the starter-site plan's bedrooms injected, the first bedroom placement stands on exactly the planned sites the "
                + "latest coupled selection round selected, in their ring stuff, and on no other site of the room. Unit tests cover "
                + "the gate and the selected operations; they cannot prove the native blueprints the selection round leads to.",
            Start = new FixtureStart
            {
                Op = LayoutFixture.GridPrepare,
                ArgsFrom = async (harness, ct) =>
                {
                    var planner = StarterSiteHost.Planner ?? throw new AcceptanceException("no starter-site planner is wired (StarterSiteHost.Planner)");
                    site = await planner.PlanAsync(harness, ct).ConfigureAwait(false);
                    return StarterSite.SiteArgs(site, 9, bedrooms: false);
                },
                Args = new JsonObject { ["sleepingSpots"] = 8, ["stoneBlocks"] = 400, ["builders"] = true },
                On = new SaveStart { Name = SustainedFoodCase.BaselineSave },
            },
            Keep = ["Food"],
            Serve = new ServeSpec
            {
                Families = ["shelter", "expansion", "sleeping"],
                NativeTimeout = TimeSpan.FromSeconds(30),
                Prefix = "sleeping-selected-bedroom",
            },
            Budget = TimeSpan.FromMinutes(3),
            Crew = new Crew { Size = 3 },
            Reason = "one watch: from the injected plan to the first bedroom placement",
            Run = (s, ct) => RunAsync(s, site ?? throw new AcceptanceException("the start derived no starter-site plan"), ct),
        };
    }

    private sealed record Placement(int Room, long ReviewTick, string Method);

    private static async Task RunAsync(ISession s, StarterSitePlan site, CancellationToken cancellationToken)
    {
        s.Report["fixture"] = s.Prepared?.DeepClone();
        var bedrooms = site.Plan.Rooms.Where(r => r.Role == PolicyFunctions.PlannedBedroom).Select(r => r.Id).ToArray();
        s.Report["planned_bedrooms"] = new JsonArray([.. bedrooms.Select(id => (JsonNode?)id)]);
        if (bedrooms.Length == 0)
        {
            throw new AcceptanceException("the starter-site plan holds no bedroom room to inject");
        }

        await InjectPlanAsync(s, site.Plan, cancellationToken).ConfigureAwait(false);
        var scope = await ScopeAsync(s.Harness, cancellationToken).ConfigureAwait(false);
        var before = (await BuildingsAsync(s.Harness, "buildings-before", scope, cancellationToken).ConfigureAwait(false))
            .Select(b => b.Building?.Id ?? "").ToHashSet(StringComparer.Ordinal);

        Placement? placed = null;
        var service = ObservedSession.Watched(s, await s.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false));
        try
        {
            await SustainedFood.WatchAsync(s.Config, service, new WatchConfig
            {
                Watch = TimeSpan.FromSeconds(90),
                PollTicks = 250,
                Extra = [ShelterFamily.Sleeping.Value],
                Until = sample => (placed = Placed(sample[ShelterFamily.Sleeping.Value].AsMap())) is not null,
            }, s.Report, new List<JsonObject>(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Stopped at once: the review after the placement sees the room started and places the rest of it.
            if (await service.StopAsync().ConfigureAwait(false) is { } keep)
            {
                s.Report["authority_reacquisitions"] = keep;
            }
        }

        if (placed is not { } placement)
        {
            throw new AcceptanceException("no bedroom shell placement within the watch");
        }

        s.Report["placement"] = new JsonObject { ["room"] = placement.Room, ["review_tick"] = placement.ReviewTick, ["method"] = placement.Method };
        var selected = SelectedSites(ServiceLogs.ReadLogs(service.LogPath), placement, s.Report);
        var (_, plan) = await ObservedSession.ReadLayoutPlanAsync(s.Config, cancellationToken).ConfigureAwait(false);
        var room = plan?.Rooms.FirstOrDefault(r => r.Id == placement.Room)
            ?? throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the journal's layout plan holds no room {placement.Room}"));
        var after = await BuildingsAsync(s.Reattach(), "buildings-after", scope, cancellationToken).ConfigureAwait(false);
        AssertPlaced(plan!, room, after.Where(b => !before.Contains(b.Building?.Id ?? "")), selected, s.Report);
    }

    /// <summary>The first bedroom shell build or install plan of the sample whose placement native was handed, with the review tick it was read at.</summary>
    private static Placement? Placed(JsonObject? sample)
    {
        if (sample is null)
        {
            return null;
        }

        foreach (var raw in sample["plans"].AsSlice().Concat(sample["retired_plans"].AsSlice()))
        {
            var plan = raw.AsMap();
            var method = plan?["method"].AsString() ?? "";
            if (!method.StartsWith(ShellRoom, StringComparison.Ordinal) || !(method.Contains("-build-", StringComparison.Ordinal) || method.Contains("-install-", StringComparison.Ordinal)))
            {
                continue;
            }

            var stages = plan!["stages"].AsMap();
            var digits = new string([.. method[ShellRoom.Length..].TakeWhile(char.IsAsciiDigit)]);
            if (Handed.Sum(stage => stages?[stage].AsNumber() ?? 0) > 0 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var room))
            {
                return new Placement(room, (long)sample["review_tick"].AsNumber(), method);
            }
        }

        return null;
    }

    /// <summary>
    /// The sites of the placed room the latest coupled selection round at or before the placement's review selected
    /// (its planned_rooms completed_sites); throws when no round ran by then or the latest selected none of the room.
    /// </summary>
    private static SortedSet<string> SelectedSites(IReadOnlyList<LogRow> rows, Placement placement, Report report)
    {
        IReadOnlyDictionary<string, object?>? latest = null;
        foreach (var row in rows.Where(r => r.Kind == "coupled_selection"))
        {
            var fields = row.Fields();
            if (fields.GetValueOrDefault("observed_tick") is double tick && tick <= placement.ReviewTick)
            {
                latest = fields;
            }
        }

        if (latest is null)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"room {placement.Room} was placed by tick {placement.ReviewTick} with no coupled selection round before it"));
        }

        var sites = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in latest.GetValueOrDefault("planned_rooms") as IEnumerable<object?> ?? [])
        {
            if (entry is IReadOnlyDictionary<string, object?> planned && planned.GetValueOrDefault("room") is double id && (int)id == placement.Room)
            {
                sites.UnionWith((planned.GetValueOrDefault("completed_sites") as IEnumerable<object?> ?? []).OfType<string>());
            }
        }

        report["selection"] = new JsonObject
        {
            ["target"] = latest.GetValueOrDefault(Telemetry.Decision.TargetKey) as string ?? "",
            ["observed_tick"] = latest.GetValueOrDefault("observed_tick") as double? ?? 0,
            ["sites"] = new JsonArray([.. sites.Select(id => (JsonNode?)id)]),
        };
        if (sites.Count == 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"room {placement.Room} was placed though the latest coupled selection round before it selected none of planned/room{placement.Room}/"));
        }

        return sites;
    }

    /// <summary>
    /// Each new player building on the room's own ring or interior maps to the planned site it stands for (a ring
    /// piece to its wall, door or vent site in its stuff; an interior piece to its build site by def and anchor, else
    /// its floor site), and that site must be selected; every selected site but the roof must have one.
    /// </summary>
    private static void AssertPlaced(LayoutPlan plan, PlannedRoom room, IEnumerable<Wired.BuildingState> placed, SortedSet<string> selected, Report report)
    {
        var (wallDef, doorDef) = room.RingDefs();
        var prefix = "planned/room" + room.Id.ToString(CultureInfo.InvariantCulture);
        // A ring cell two rooms owe is the lower room's site (PlannedSitesOf).
        var shared = plan.Rooms.Where(r => r.Id < room.Id && r.Id != 0).SelectMany(r => r.Region.Walls().Cells()).ToHashSet();
        var ring = room.Region.Walls().Cells().Where(c => !shared.Contains(c)).ToHashSet();
        var interior = room.Region.Cells().ToHashSet();
        var met = new SortedSet<string>(StringComparer.Ordinal);
        var stray = new JsonArray();
        var rows = new JsonArray();
        foreach (var b in placed)
        {
            if (b.Building?.Position is not { X: { } x, Z: { } z })
            {
                continue;
            }

            var at = new Cell(x, z);
            var def = (b.Status == Wired.BuildingStatus.Built ? b.Building.DefName : b.BuildDefName ?? b.Building.DefName) ?? "";
            var stuff = b.Stuff ?? "";
            string[] candidates;
            if (ring.Contains(at))
            {
                var kind = def == wallDef ? "wall" : def == doorDef ? "door" : "vent";
                candidates = [prefix + "/" + kind + (stuff.Length > 0 ? "/" + stuff : "")];
            }
            else if (interior.Contains(at))
            {
                candidates = [string.Create(CultureInfo.InvariantCulture, $"{prefix}/build/{def}/{x}/{z}"), prefix + "/floor/" + def];
            }
            else
            {
                continue;
            }

            var site = candidates.FirstOrDefault(selected.Contains);
            rows.Add(new JsonObject { ["def"] = def, ["stuff"] = stuff, ["status"] = b.Status.ToString(), ["x"] = x, ["z"] = z, ["site"] = site ?? candidates[0] });
            if (site is null)
            {
                stray.Add(candidates[0]);
            }
            else
            {
                met.Add(site);
            }
        }

        var missing = selected.Where(id => id != prefix + "/roof" && !met.Contains(id)).ToArray();
        report["placed"] = rows;
        report["unselected_placed"] = stray.DeepClone();
        report["selected_unplaced"] = new JsonArray([.. missing.Select(id => (JsonNode?)id)]);
        if (rows.Count == 0)
        {
            throw new AcceptanceException($"{prefix}: the placement left no building on the room");
        }

        if (stray.Count > 0 || missing.Length > 0)
        {
            throw new AcceptanceException($"{prefix}: placed sites differ from the selected ones: unselected placed {stray.ToJsonString()}, selected unplaced [{string.Join(", ", missing)}]");
        }
    }

    /// <summary>Replaces the save's governor layout plan with <paramref name="plan"/> (the service reads it at start), keeping every other blob.</summary>
    private static async Task InjectPlanAsync(ISession s, LayoutPlan plan, CancellationToken cancellationToken)
    {
        var loaded = Harness.Outcome(await s.Harness.WireAsync("read-governor-state", "lifecycle_read_governor_state", new JsonObject(), cancellationToken).ConfigureAwait(false), "loaded").Value;
        var blobs = new JsonObject();
        foreach (var (key, value) in loaded["blobs"].AsMap() ?? [])
        {
            blobs[key] = value.AsString();
        }

        blobs[Store.Store.GovernorLayoutPlanKey] = new JsonObject
        {
            ["schemaVersion"] = Store.Store.GovernorStateSchemaVersion,
            ["scope"] = new JsonObject { ["colony"] = s.Identity["colonyId"].AsString(), ["map"] = s.Identity["mapId"].AsNumber(), ["tick"] = 0 },
            ["record"] = JsonNode.Parse(StoreJson.Serialize(plan)),
        }.ToJsonString();
        _ = Harness.Outcome(await s.Harness.WireAsync("put-governor-state", "lifecycle_put_governor_state_batch", new JsonObject { ["blobs"] = blobs }, cancellationToken).ConfigureAwait(false), "loaded");
    }

    private static async Task<JsonObject> ScopeAsync(Harness h, CancellationToken cancellationToken)
    {
        var loaded = Harness.Outcome(await h.WireAsync("identity", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false), "loaded").Value;
        return new JsonObject { ["expectedIdentity"] = loaded["context"].AsMap()?["identity"]?.DeepClone() };
    }

    /// <summary>Every player building, blueprint and frame on the map, as the typed snapshot.</summary>
    private static async Task<IReadOnlyList<Wired.BuildingState>> BuildingsAsync(Harness h, string label, JsonObject scope, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(label, "observations_list_buildings", new JsonObject
        {
            ["scope"] = scope.DeepClone(),
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            return [.. WireCodec.ToRecord<Wired.BuildingsSnapshot>(Harness.Outcome(reply, "observed").Value).Buildings];
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"{label}: {e.Message}", e);
        }
    }
}
