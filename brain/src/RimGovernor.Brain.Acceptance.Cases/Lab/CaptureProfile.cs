using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lab/capture-profile</c> and <c>lab/capture-profile-250</c>: the read-only
/// <c>rimgovernor/acceptance_read_capture_profile</c> tool times and counts the managed allocation of the snapshot frame
/// capture, its sections, the cell grid read, each game API the grid read calls over the whole map and the brain's grid
/// reads, and the case files the result in the report. A unit test cannot run the game's map APIs.
/// Wall time is load-sensitive and only reported; allocated bytes are not, so the 250x250 case holds ceilings on them.
/// </summary>
internal static class LabCaptureProfile
{
    private const string Tool = "rimgovernor/acceptance_read_capture_profile";

    // Median allocated bytes per span on the 250x250 debug start, set well above what the forecast's id-index read
    // allocates (capture 15.9 MB, food 0.26 MB, needs 0.37 MB) and well below the per-thing load-id scans it replaced
    // (57.5 MB, 34.7 MB, 7.4 MB).
    private static readonly Dictionary<string, long> Ceilings250 = new(StringComparer.Ordinal)
    {
        ["capture.total"] = 28_000_000,
        ["capture.detail.cf.fc.food"] = 4_000_000,
        ["capture.detail.cf.fc.needs"] = 2_000_000,
    };

    internal static IEnumerable<AcceptanceCase> Cases()
    {
        yield return Case("lab/capture-profile", new LabStart { Colonists = 3 }, "the 100x100 Soil lab with three colonists", "", new());
        yield return Case("lab/capture-profile-250", new DebugStart { Size = new DebugStartSize { MapSize = 250, PlanetCoverage = 0.05 } }, "a generated 250x250 debug start",
            "the capture cost scales with map cells and things; a played colony's default 250x250 map with its rock, plants and animals is the size being measured",
            Ceilings250);
    }

    private static AcceptanceCase Case(string name, Start start, string where, string reason, Dictionary<string, long> ceilings) => new()
    {
        Reason = reason,
        Name = name,
        Scope = "Frame capture profile on " + where + ": wall time and allocated bytes of the snapshot capture, "
            + "its sections, the cell grid read, each grid column's game API pass and the brain's grid reads"
            + (ceilings.Count == 0 ? " (measurement, not a gate)." : "; median allocated bytes stay under each span's ceiling."),
        Start = start,
        RequiredOps = [Tool],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = (s, ct) => RunAsync(s, ceilings, ct),
    };

    private static async Task RunAsync(ISession s, Dictionary<string, long> ceilings, CancellationToken cancellationToken)
    {
        var reply = await s.Harness.CallAsync("capture-profile", Tool, new JsonObject { ["iterations"] = 5 }, cancellationToken).ConfigureAwait(false);
        if (reply["payload"] is not JsonValue value || !value.TryGetValue<string>(out var payload) || JsonNode.Parse(payload) is not JsonObject profile)
        {
            throw new AcceptanceException($"capture profile: reply carried no payload: {reply.ToJsonString()}");
        }

        s.Report["capture_profile"] = profile;
        if (profile["spans"] is not JsonObject spans || spans["capture.total"] is null || spans["grid.read"] is null)
        {
            throw new AcceptanceException($"capture profile: no capture spans: {payload}");
        }

        var over = ceilings.Select(c => (c.Key, c.Value, Bytes: spans[c.Key]?["bytes_median"]?.GetValue<long>()))
            .Where(c => c.Bytes is not { } b || b < 0 || b > c.Value)
            .Select(c => $"{c.Key} {(c.Bytes is { } b && b >= 0 ? b.ToString("N0") + " B" : "unmeasured")} > ceiling {c.Value:N0} B").ToList();
        if (over.Count != 0)
        {
            throw new AcceptanceException("capture profile: allocation over ceiling: " + string.Join("; ", over));
        }
    }
}
