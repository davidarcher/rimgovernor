using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>farm/select-hydroponics</c> (Go <c>select.go</c>): the live service runs with the field family on, the field planner
/// runs, and the site-type selections it traced are asserted: which kind and crop won, that the winner carries a per-term
/// score breakdown, and that every unplantable candidate states its reason. The case stages a controlled environment (a
/// roofed room with a running sun lamp under a cold snap that closes the outdoor season) through the private
/// <c>test/farm_environment_prepare</c> fixture, gates rice behind unfinished research and expects potatoes, proving the basin
/// candidate scores every Hydroponic crop and that a built basin is re-cropped from its default rice to the winner.
/// </summary>
internal static class FarmSelectHydroponics
{
    private const string FixturePrepare = "test/farm_environment_prepare";
    private const string FixtureObserve = "test/farm_environment_observe";

    private const string Environment = "hydroponics";

    /// <summary>How long the field planner gets to trace a selection.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(4);

    private static readonly FarmExpectation Expect = new() { Kind = "hydroponics", Crop = "Plant_Potato", MinCells = 1, Terms = ["risk-frost"] };

    internal static AcceptanceCase Case() => new()
    {
        Name = "farm/select-" + Environment,
        Scope = "Field site-type selection against the " + Baselines.BaselineName + " save under the live field planner: "
            + "every traced selection must choose the expected site kind/crop with a per-term breakdown and reasons "
            + "for every unplantable candidate (issue #3 M4).",
        Start = new FixtureStart
        {
            Op = FixturePrepare,
            Args = new JsonObject { ["scenario"] = Environment, ["unavailableCrops"] = "Plant_Rice,Plant_Strawberry" },
            On = new SaveStart { Name = Baselines.BaselineName },
        },
        Keep = [NeedDef.Food.Value],

        // The field family alone keeps the whole step budget for the selection under test; the planner explains its
        // selection in fields_select rows.
        Serve = new ServeSpec { Families = ["field"], NativeTimeout = TimeSpan.FromSeconds(30), Prefix = "farm-select" },
        Budget = TimeSpan.FromMinutes(10),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var fixture = s.Prepared ?? [];

        // An unlit lamp (outside its sun schedule at this save's hour) leaves no controlled kind plantable; fail before the watch.
        if (fixture["lampScheduled"].AsBool() != true)
        {
            throw new AcceptanceException($"fixture sun lamp is outside its schedule at this save's hour: {fixture.ToJson()}");
        }

        if (fixture["lampPowered"].AsBool() != true)
        {
            throw new AcceptanceException($"fixture sun lamp is unpowered: {fixture.ToJson()}");
        }

        var outdoor = fixture["outdoorTemperatureC"].AsNumber();
        if (outdoor >= 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"outdoor temperature {outdoor:F1} C did not close the growing season: {fixture.ToJson()}"));
        }

        // The season must stay closed through the watch, not only at the save's hour: at the day's peak the planner
        // rightly goes outdoors once the room is full.
        var peak = fixture["outdoorPeakTemperatureC"].AsNumber();
        if (peak >= 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"outdoor peak temperature {peak:F1} C reopens the growing season during the watch: {fixture.ToJson()}"));
        }

        var observation = new SustainedObservation
        {
            Watch = new WatchConfig { Watch = Window },
            Audit = async (h, report, ct) =>
            {
                var interior = fixture["interior"].AsMap() ?? [];
                var observed = await h.CallAsync(
                    "observe",
                    FixtureObserve,
                    new JsonObject
                    {
                        ["minX"] = (int)interior["minX"].AsNumber(),
                        ["minZ"] = (int)interior["minZ"].AsNumber(),
                        ["maxX"] = (int)interior["maxX"].AsNumber(),
                        ["maxZ"] = (int)interior["maxZ"].AsNumber(),
                    },
                    ct).ConfigureAwait(false);
                report["enacted"] = observed;
                var basins = observed["basins"].AsSlice();
                if (basins.Count == 0)
                {
                    throw new AcceptanceException($"no hydroponics basin placed inside the fixture room after the watch: {observed.ToJson()}");
                }

                if (Expect.Crop.Length > 0)
                {
                    BasinSows(basins, Expect.Crop);
                }
            },
        };
        _ = await SustainedFood.ObserveAsync(ObservedSession.Of(s), observation, cancellationToken).ConfigureAwait(false);

        var rows = ServiceLogs.ReadLogs(ServiceLogs.ServiceLogsPath(s.Config.Output));
        var selections = FarmSelect.Parse(rows);
        s.Report["selections"] = selections.Count;
        var last = FarmSelect.Check(selections, Expect);
        s.Report["selection"] = JsonSerializer.SerializeToNode(last);
    }

    /// <summary>
    /// Requires at least one built basin sowing the expected crop: a new basin sows its definition's default, so a built
    /// basin still on another crop means the re-crop patch never landed.
    /// </summary>
    private static void BasinSows(IReadOnlyList<JsonNode?> basins, string crop)
    {
        var built = 0;
        foreach (var row in basins)
        {
            var basin = row.AsMap() ?? [];
            if (basin["stage"].AsString() != "built")
            {
                continue;
            }

            built++;
            if (basin["crop"].AsString() == crop)
            {
                return;
            }
        }

        var shown = new JsonArray([.. basins.Select(b => b?.DeepClone())]).ToJson();
        if (built == 0)
        {
            throw new AcceptanceException($"no basin finished construction inside the fixture room during the watch; {basins.Count} placed: {shown}");
        }

        throw new AcceptanceException($"{built} built basins but none sows {crop}: {shown}");
    }
}
