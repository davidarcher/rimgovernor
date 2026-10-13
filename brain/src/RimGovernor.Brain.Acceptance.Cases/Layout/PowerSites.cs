using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>layout/power-sites</c> (Go <c>power_sites.go</c>): the planned power sites against the native footprint. On the
/// layout grid start the first review records a layout plan; every PlannedPowerSites battery and solar site is
/// previewed at its centre and rotation, and the footprint RimWorld reports must be exactly the planned area. The
/// battery room's first BatterySlots row must be the one nearest its door.
/// </summary>
internal static class LayoutPowerSites
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "layout/power-sites",
        Scope = "Issue #838: on the layout/grid start the recorded layout plan's battery and solar sites (PlannedPowerSites) " +
            "preview natively with exactly the planned footprint, and the battery room's first slot row is the one nearest its door.",
        Start = new FixtureStart
        {
            Op = LayoutFixture.GridPrepare,
            ArgsFrom = LayoutSiteHost.ArgsAsync,
            Args = new JsonObject { ["sleepingSpots"] = LayoutFixture.Bunks, ["stoneBlocks"] = LayoutFixture.Blocks },
            On = new SaveStart { Name = Baselines.BaselineName },
        },
        Keep = [NeedDef.Food.Value],
        Serve = new ServeSpec
        {
            Families = ["shelter", "expansion"],
            NativeTimeout = TimeSpan.FromSeconds(30),
            Prefix = "layout-power",
        },
        Budget = TimeSpan.FromMinutes(3),
        Crew = new Crew { Size = 3 },
        Reason = "one short watch until the first review records a layout plan, then native previews",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        _ = await SustainedFood.ObserveAsync(
            ObservedSession.Of(s),
            new SustainedObservation
            {
                Watch = new WatchConfig
                {
                    Watch = TimeSpan.FromMinutes(1),
                    Extra = [PolicyFunctions.MaintainHousing.Value],
                    Until = sample => LayoutFixture.Planned(sample[PolicyFunctions.MaintainHousing.Value].AsMap(), IsShellMethod, complete: false),
                },
                Audit = (h, rep, ct) => AuditAsync(s, h, rep, ct),
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task AuditAsync(ISession s, Harness h, Report report, CancellationToken cancellationToken)
    {
        LayoutPlan layout;
        try
        {
            using var journal = await ServiceJournal.OpenWithRetryAsync(ServiceJournal.PathOf(s.Config), cancellationToken).ConfigureAwait(false);
            var review = await journal.LoadRoundsAsync(cancellationToken).ConfigureAwait(false);
            var record = await journal.LayoutPlanAsync(review.Snapshot, review.Tick, cancellationToken).ConfigureAwait(false);
            layout = record?.Plan ?? throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"no layout plan recorded by tick {review.Tick.Value}"));
        }
        catch (AcceptanceException)
        {
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new AcceptanceException("reopen journal: " + e.Message, e);
        }

        var identity = s.Identity;
        var checkedSites = new JsonArray();
        foreach (var def in new[] { PolicyConstants.BatteryDefinition, PolicyFunctions.SolarDefinition })
        {
            var sites = PolicyFunctions.PlannedPowerSites(layout, def);
            if (sites.Count == 0)
            {
                throw new AcceptanceException($"the layout plan has no {def} site");
            }

            for (var i = 0; i < sites.Count; i++)
            {
                var site = sites[i];
                var placement = new JsonObject
                {
                    ["defName"] = def,
                    ["x"] = site.Cell.X,
                    ["z"] = site.Cell.Z,
                    ["rotation"] = "ROTATION_" + RotationName(site.Rotation),
                };
                var reply = await h.WireAsync(
                    string.Create(CultureInfo.InvariantCulture, $"preview-{def}-{i}"),
                    "placement_preview",
                    new JsonObject { ["identity"] = identity.DeepClone(), ["placements"] = new JsonArray(placement) },
                    cancellationToken).ConfigureAwait(false);
                var batch = reply["batch"].AsMap();
                JsonObject? evaluation = null;
                var results = batch?["results"].AsSlice() ?? [];
                if (results.Count == 1)
                {
                    evaluation = results[0].AsMap()?["evaluated"].AsMap();
                }

                var rotations = evaluation?["rotations"].AsSlice() ?? [];
                if (rotations.Count != 1)
                {
                    throw new AcceptanceException($"{def} site {i}: expected one evaluated rotation, got {reply.ToJson()}");
                }

                var row = rotations[0].AsMap() ?? [];
                var got = new HashSet<Cell>();
                foreach (var raw in row["occupiedCells"].AsSlice())
                {
                    var c = raw.AsMap() ?? [];
                    got.Add(new Cell((int)c["x"].AsNumber(), (int)c["z"].AsNumber()));
                }

                var want = site.Area;
                var match = got.Count == want.Width * want.Height;
                for (var x = want.X; match && x < want.X + want.Width; x++)
                {
                    for (var z = want.Z; z < want.Z + want.Height; z++)
                    {
                        match = match && got.Contains(new Cell(x, z));
                    }
                }

                checkedSites.Add(new JsonObject
                {
                    ["def"] = def,
                    ["cell"] = CaseSupport.Cell(site.Cell.X, site.Cell.Z),
                    ["rotation"] = site.Rotation.ToString().ToLowerInvariant(),
                    ["area"] = RectJson(want),
                    ["occupied"] = got.Count,
                    ["match"] = match,
                });
                if (!match)
                {
                    var footprint = string.Join(' ', got.OrderBy(c => c.Z).ThenBy(c => c.X).Select(c => c.ToString()));
                    throw new AcceptanceException($"{def} site {i} at {site.Cell} {site.Rotation.ToString().ToLowerInvariant()}: native footprint [{footprint}] is not the planned area {want}");
                }
            }
        }

        report["sites"] = checkedSites;
        foreach (var room in layout.Rooms)
        {
            if (room.Role != PolicyFunctions.PlannedBattery)
            {
                continue;
            }

            var slots = PolicyFunctions.BatterySlots(room);

            // A room on a crossing (east or west door) steps its rows along x.
            Func<Rectangle, int> dist = r => Math.Max(r.Z - room.Door.Z, room.Door.Z - r.Z);
            if (room.DoorRot is Rotation.East or Rotation.West)
            {
                dist = r => Math.Max(r.X - room.Door.X, room.Door.X - r.X);
            }

            var doorRot = room.DoorRot?.ToString().ToLowerInvariant() ?? "";
            report["battery_room"] = new JsonObject
            {
                ["door"] = CaseSupport.Cell(room.Door.X, room.Door.Z),
                ["door_rot"] = doorRot,
                ["interior"] = RectJson(room.Region.Bounds()),
                ["first_slot"] = RectJson(slots[0]),
                ["last_slot"] = RectJson(slots[^1]),
            };
            if (dist(slots[0]) > dist(slots[^1]))
            {
                throw new AcceptanceException($"battery room door {room.Door} ({doorRot}): first slot {slots[0]} is farther from the door than the last {slots[^1]}");
            }
        }
    }

    private static JsonObject RectJson(Rectangle r) => new() { ["x"] = r.X, ["z"] = r.Z, ["width"] = r.Width, ["height"] = r.Height };

    private static string RotationName(Rotation r) => r switch
    {
        Rotation.East => "EAST",
        Rotation.South => "SOUTH",
        Rotation.West => "WEST",
        _ => "NORTH",
    };

    /// <summary>A planned room's ring method: a build wave of a "role-shell-x-z" reconcile (Go <c>path.Match("*-shell-*-build-*")</c>).</summary>
    private static bool IsShellMethod(string method)
    {
        var shell = method.IndexOf("-shell-", StringComparison.Ordinal);
        return shell >= 0 && method.IndexOf("-build-", shell + "-shell-".Length, StringComparison.Ordinal) >= 0;
    }
}
