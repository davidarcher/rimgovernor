using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lab/capture-profile-250</c>: the read-only <c>rimgovernor/acceptance_read_capture_profile</c> tool times and counts
/// the managed allocation of the snapshot frame capture, its sections, the cell grid read, each game API the grid read
/// calls over the whole map and the brain's grid reads. Wall time is load-sensitive and only reported; allocated bytes are
/// not, so the case holds ceilings on them. A unit test cannot run the game's map APIs.
/// </summary>
internal static class LabCaptureProfile
{
    private const string Tool = "rimgovernor/acceptance_read_capture_profile";

    // Median allocated bytes per span on the 250x250 debug start, set well above what the forecast's id-index read
    // allocates (capture 15.9 MB, food 0.26 MB, needs 0.37 MB) and well below the per-thing load-id scans it replaced
    // (57.5 MB, 34.7 MB, 7.4 MB).
    private static readonly Dictionary<string, long> Ceilings = new(StringComparer.Ordinal)
    {
        ["capture.total"] = 28_000_000,
        ["capture.detail.cf.fc.food"] = 4_000_000,
        ["capture.detail.cf.fc.needs"] = 2_000_000,
    };

    // The production read replies its profile as a JSON string in "payload".
    private static readonly FixtureOp<int, (Fx.CaptureProfileRead Profile, JsonNode? Raw)> Profile = new(
        Tool,
        iterations => ServedOps.Args(("iterations", iterations)),
        (label, reply) =>
        {
            JsonNode? node = null;
            if (reply?.TryGetPropertyValue("payload", out node) != true || node is not JsonValue value || !value.TryGetValue<string>(out var payload))
            {
                throw new AcceptanceException($"{label}: {Tool} replied no payload: {reply?.ToJsonString()}");
            }

            try
            {
                return (Fx.FixtureJson.Deserialize<Fx.CaptureProfileRead>(payload), JsonNode.Parse(payload));
            }
            catch (JsonException e)
            {
                throw new AcceptanceException($"{label}: {Tool} payload does not decode: {e.Message}", e);
            }
        });

    internal static TypedCase Case() => new()
    {
        Name = "lab/capture-profile-250",
        Scope = "Frame capture profile on a generated 250x250 debug start (the default size of a played colony's map, with its "
            + "rock, plants and animals): the median allocated bytes of the snapshot capture and its food and needs forecast "
            + "sections stay under each span's ceiling; wall time and the other spans are reported.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new DebugStart { Size = new DebugStartSize { MapSize = 250, PlanetCoverage = 0.05 } },
        Ops = [Tool],
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var (profile, raw) = await s.FixtureAsync("capture-profile", Profile, 5, cancellationToken).ConfigureAwait(false);
        s.Record("capture_profile", raw);
        if (!profile.Spans.ContainsKey("capture.total") || !profile.Spans.ContainsKey("grid.read"))
        {
            throw new AcceptanceException("capture profile: no capture spans");
        }

        var over = Ceilings
            .Select(c => (c.Key, Ceiling: c.Value, Bytes: profile.Spans.TryGetValue(c.Key, out var span) ? span.BytesMedian : -1))
            .Where(c => c.Bytes < 0 || c.Bytes > c.Ceiling)
            .Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Key} {(c.Bytes >= 0 ? c.Bytes.ToString("N0", CultureInfo.InvariantCulture) + " B" : "unmeasured")} > ceiling {c.Ceiling:N0} B"))
            .ToList();
        if (over.Count != 0)
        {
            throw new AcceptanceException("capture profile: allocation over ceiling: " + string.Join("; ", over));
        }
    }
}
