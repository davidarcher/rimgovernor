using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>pawn/nickname</c> (Go <c>nickname.go</c>).</summary>
internal static class PawnNickname
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "pawn/nickname",
        Scope = "The newest fixture colonist given the oldest one's nickname is renamed by EnsureWorkAssignments through a PawnSettingsIntent nickname (#1310), and the population read's owned names show two distinct, unnumbered short names. Native contract: the owned-name census and the name-bank draw; the collision rule is policy/pawn_names_test.go.",
        Start = new FixtureStart { Op = "test/duplicate_nickname", On = new LabStart() },
        Serve = PawnSupport.WorkSpec,
        Budget = TimeSpan.FromMinutes(4),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    /// <summary>Each owned pawn's short name by pawn id (the population read's <c>ownedNames</c>).</summary>
    private static async Task<Dictionary<string, string>> ShortNamesAsync(ISession s, string label, CancellationToken cancellationToken)
    {
        var request = new JsonObject { ["scope"] = PawnSupport.Scope(s) };
        var reply = await s.Harness.WireAsync(label, "observations_read_population", request, cancellationToken).ConfigureAwait(false);
        var observed = WallCensus.Observed(label, reply);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in observed["ownedNames"].AsSlice())
        {
            var owned = raw.AsMap() ?? [];
            names[owned["pawnId"].AsString()] = owned["shortName"].AsString();
        }

        return names;
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var pawnId = PawnSupport.Fixture(s, "pawn", "duplicate pair");
        var keeperId = PawnSupport.Fixture(s, "keeper", "duplicate pair");
        var name = PawnSupport.Fixture(s, "name", "duplicate pair");
        s.Report["pawn"] = pawnId;
        s.Report["name"] = name;
        await PawnSupport.SettleAsync(
            s,
            async ct =>
            {
                var short1 = await ShortNamesAsync(s, "nickname-wait", ct).ConfigureAwait(false);
                var current = short1.GetValueOrDefault(pawnId, "");
                return current.Length > 0 && !string.Equals(current, name, StringComparison.OrdinalIgnoreCase) ? ("rename applied", true) : ("waiting for the rename", false);
            },
            cancellationToken).ConfigureAwait(false);
        var shortNames = await ShortNamesAsync(s, "nickname", cancellationToken).ConfigureAwait(false);
        var got = shortNames.GetValueOrDefault(pawnId, "");
        s.Report["renamed"] = got;
        var keeper = shortNames.GetValueOrDefault(keeperId, "");
        if (keeper != name)
        {
            throw new AcceptanceException($"keeper short name \"{keeper}\", want \"{name}\"");
        }

        if (got.Length == 0 || string.Equals(got, name, StringComparison.OrdinalIgnoreCase) || got.Any(char.IsAsciiDigit))
        {
            throw new AcceptanceException($"renamed short name \"{got}\", want a fresh unnumbered name other than \"{name}\"");
        }
    }
}
