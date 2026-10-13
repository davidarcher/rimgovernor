using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The pieces the <c>pawn/*</c> serve cases share. The Go cases waited on the controller's plan journal
/// (<c>journal.PlanHistoryWithMethods</c>) for a completed PawnSettings action; the in-mod brain exposes no plan history
/// to the harness, so each case now polls the native readback its own final assertion checks (the same game state the
/// completed action writes), through the wire reads below, with the same ceiling, stall and interval.
/// </summary>
internal static class PawnSupport
{
    /// <summary>The routine family set every serve case here runs (Go <c>routinefamily.Work</c>).</summary>
    internal static readonly ServeSpec WorkSpec = new() { Families = ["work"] };

    internal static readonly TimeSpan SettleCeiling = TimeSpan.FromMinutes(2);

    internal static readonly TimeSpan SettleStall = TimeSpan.FromSeconds(60);

    /// <summary>A read scope pinned to the session's loaded identity.</summary>
    internal static JsonObject Scope(ISession s) => new() { ["expectedIdentity"] = s.Identity.DeepClone() };

    /// <summary>
    /// Serves the case's spec, acquires authority, keeps it, polls <paramref name="probe"/> until it reports done, then
    /// stops the service and takes the game back. The service is stopped on every path.
    /// </summary>
    internal static async Task SettleAsync(ISession s, Probe probe, CancellationToken cancellationToken)
    {
        var service = await s.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            service.KeepAuthority(cancellationToken);
            var wait = new Wait { Ceiling = SettleCeiling, Stall = SettleStall, Interval = TimeSpan.FromSeconds(1), Terminal = service.Exited };
            await wait.WaitProgressAsync(probe, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }

        _ = s.Reattach();
    }

    /// <summary>The fixture id under <paramref name="key"/>, or an exception when the fixture returned none.</summary>
    internal static string Fixture(ISession s, string key, string what)
    {
        var id = s.Prepared?[key].AsString() ?? "";
        return id.Length == 0 ? throw new AcceptanceException($"fixture returned no {what}") : id;
    }

    /// <summary>
    /// The pawn rows <c>observations_list_pawns</c> answers for exactly <paramref name="ids"/>. <paramref name="rounds"/>
    /// asks for the routine census details (Go <c>ReadRoundsPawns</c>: combat, work, care, schedule and social), else
    /// no detail family (Go <c>ReadPawns</c>).
    /// </summary>
    internal static async Task<List<JsonObject>> ReadPawnsAsync(Harness h, ISession s, string label, IReadOnlyList<string> ids, bool rounds, CancellationToken cancellationToken)
    {
        var details = new JsonObject
        {
            ["needs"] = rounds,
            ["health"] = rounds,
            ["equipment"] = rounds,
            ["biography"] = rounds,
            ["settings"] = rounds,
            ["social"] = rounds,
            ["animals"] = rounds,
        };
        if (rounds)
        {
            details["work"] = true;
            details["schedule"] = true;
        }

        var request = new JsonObject
        {
            ["scope"] = Scope(s),
            ["filter"] = new JsonObject { ["ids"] = new JsonArray([.. ids.Select(id => (JsonNode?)JsonValue.Create(id))]), ["includeDead"] = true },
            ["details"] = details,
        };
        var reply = await h.WireAsync(label, "observations_list_pawns", request, cancellationToken).ConfigureAwait(false);
        var observed = WallCensus.Observed(label, reply);
        return [.. observed["pawns"].AsSlice().Select(row => row.AsMap() ?? [])];
    }

    /// <summary>The single fixture pawn row of a pawn read.</summary>
    internal static async Task<JsonObject> ReadPawnAsync(Harness h, ISession s, string label, string pawnId, bool rounds, CancellationToken cancellationToken)
    {
        var rows = await ReadPawnsAsync(h, s, label, [pawnId], rounds, cancellationToken).ConfigureAwait(false);
        return rows.Count == 1 && rows[0]["pawn"].Member("id").AsString() == pawnId ? rows[0] : throw new AcceptanceException("fixture pawn missing from pawn read");
    }

    /// <summary>The colony read's observed policy facts (food, drug and reading policy rows).</summary>
    internal static async Task<JsonObject> PolicyFactsAsync(Harness h, ISession s, string label, CancellationToken cancellationToken)
    {
        var request = new JsonObject { ["scope"] = Scope(s), ["planning"] = false };
        var reply = await h.WireAsync(label, "observations_read_colony_facts", request, cancellationToken).ConfigureAwait(false);
        var observed = WallCensus.Observed(label, reply);
        return observed["policies"].AsMap()?["observed"].AsMap() ?? throw new AcceptanceException("colony read carried no policy facts");
    }

    /// <summary>The policy rows of <paramref name="kind"/> (<c>food</c>, <c>drug</c> or <c>reading</c>) as maps.</summary>
    internal static List<JsonObject> Policies(JsonObject facts, string kind) => [.. facts[kind].AsSlice().Select(row => row.AsMap() ?? [])];

    /// <summary>Whether <paramref name="pawn"/> holds a policy of <paramref name="kind"/> other than the database default.</summary>
    internal static bool HoldsAssigned(JsonObject facts, string kind, string pawn) =>
        Policies(facts, kind).Any(p => p["default"].AsBool() != true && p["pawnIds"].AsSlice().Any(id => id.AsString() == pawn));

    /// <summary>The ids of a policy row's pawns.</summary>
    internal static IEnumerable<string> Holders(JsonObject policy) => policy["pawnIds"].AsSlice().Select(id => id.AsString());

    /// <summary>The ids of a policy row's allowed definitions.</summary>
    internal static IEnumerable<string> Allowed(JsonObject policy) => policy["allowedDefs"].AsSlice().Select(id => id.AsString());

    /// <summary>The reason of a reply's <c>unavailable</c> member, null when it carries none.</summary>
    internal static string? UnavailableReason(JsonObject reply) => reply["unavailable"].AsMap() is { } unavailable ? unavailable["reason"].AsString() : null;

    /// <summary>A sorted, comma-joined list for the report (Go printed a map or slice with fmt.Sprint).</summary>
    internal static string Joined(IEnumerable<string> values) => string.Join(',', values.Order(StringComparer.Ordinal));

    /// <summary>A count the wire omits when zero: absent is 0 (<c>AsNumber</c> alone reads absent as -1).</summary>
    internal static double Count(JsonNode? value) => value is null ? 0 : value.AsNumber();

    /// <summary>Invariant text of a number for messages.</summary>
    internal static string Text(double value) => value.ToString(CultureInfo.InvariantCulture);
}
