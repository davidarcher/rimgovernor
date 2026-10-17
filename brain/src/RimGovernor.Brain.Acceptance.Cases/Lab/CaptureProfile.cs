using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lab/capture-profile</c> and <c>lab/capture-profile-250</c>: measurement, not a gate. The read-only
/// <c>rimgovernor/acceptance_read_capture_profile</c> tool times and counts the managed allocation of the snapshot frame
/// capture, its sections, the cell grid read, each game API the grid read calls over the whole map, the grid encode and
/// the brain's decode, and the case files the result in the report. A unit test cannot run the game's map APIs.
/// </summary>
internal static class LabCaptureProfile
{
    private const string Tool = "rimgovernor/acceptance_read_capture_profile";

    internal static IEnumerable<AcceptanceCase> Cases()
    {
        yield return Case("lab/capture-profile", new LabStart { Colonists = 3 }, "the 100x100 Soil lab with three colonists", "");
        yield return Case("lab/capture-profile-250", new DebugStart { Size = new DebugStartSize { MapSize = 250, PlanetCoverage = 0.05 } }, "a generated 250x250 debug start",
            "the capture cost scales with map cells and things; a played colony's default 250x250 map with its rock, plants and animals is the size being measured");
    }

    private static AcceptanceCase Case(string name, Start start, string where, string reason) => new()
    {
        Reason = reason,
        Name = name,
        Scope = "Frame capture profile (measurement, not a gate) on " + where + ": wall time and allocated bytes of the snapshot capture, "
            + "its sections, the cached and uncached cell grid read, each grid column's game API pass, the grid encode and the brain's decode.",
        Start = start,
        RequiredOps = [Tool],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var reply = await s.Harness.CallAsync("capture-profile", Tool, new JsonObject { ["iterations"] = 5 }, cancellationToken).ConfigureAwait(false);
        if (reply["payload"] is not JsonValue value || !value.TryGetValue<string>(out var payload) || JsonNode.Parse(payload) is not JsonObject profile)
        {
            throw new AcceptanceException($"capture profile: reply carried no payload: {reply.ToJsonString()}");
        }

        s.Report["capture_profile"] = profile;
        if (profile["spans"] is not JsonObject spans || spans["capture.total"] is null || spans["grid.read.cached"] is null)
        {
            throw new AcceptanceException($"capture profile: no capture spans: {payload}");
        }
    }
}
