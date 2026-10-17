using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>medical/tend-gates</c> (Go <c>tendgates.go</c>).</summary>
internal static class TendGates
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "medical/tend-gates",
        Scope = "list_pawns' tend detail carries the doctor gates NativeTendOperations.Prepare enforces "
            + "(pawn-control eligibility, WorkGiver_Tend capacities, pairwise reachability), so SelectTend stops "
            + "proposing doctors the native tend gate refuses (#657).",
        Start = new FixtureStart { Op = "test/medical_plague_prepare", On = new LabStart() },
        Budget = TimeSpan.FromMinutes(1),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = await Session.ReadIdentityAsync(h, "tend-gates-identity", cancellationToken).ConfigureAwait(false);
        var observed = await MedicalKit.ListPawnsAsync(h, "tend-gates-readback", identity, MedicalKit.ColonistFilter(), MedicalKit.Details(health: true, tend: true), cancellationToken).ConfigureAwait(false);
        Observe(observed);
        s.Report["tend_gates"] = observed.DeepClone();
    }

    // Checks the tend detail against the rest of each row: every colonist carries the gates, a spawned live undowned
    // colonist is control eligible, and reachability names other returned rows only -- never the row itself, and
    // symmetrically, since CanReach over a connected colony map is not directional.
    private static void Observe(JsonObject observed)
    {
        var reach = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var ids = new List<string>();
        foreach (var row in observed["pawns"].Rows())
        {
            var id = row["pawn"].RefId();
            if (id.Length == 0)
            {
                throw new AcceptanceException($"pawn row without an identity: {row.ToJson()}");
            }

            if (row["tendDoctor"].AsMap() is not { } doctor)
            {
                throw new AcceptanceException($"{id} carries no tendDoctor detail; the tend gates are unobserved");
            }

            // The writer omits false, so an absent gate is false; every gate is always read.
            var eligible = doctor["controlEligible"].AsBool() == true;
            var spawned = doctor["spawned"].AsBool() == true;
            var drafter = doctor["hasDrafter"].AsBool() == true;
            var dead = row["dead"].AsBool() == true;
            var downed = row["downed"].AsBool() == true;
            if (spawned && drafter && !dead && !downed && row["mentalState"] is null && !eligible)
            {
                throw new AcceptanceException($"{id} controlEligible = {(eligible ? "true" : "false")} for a spawned live undowned colonist");
            }

            // An unspawned row reaches nobody and one past the tend budget carries reachSkipped; neither takes part in
            // the symmetry check.
            if (!spawned || doctor["reachSkipped"].AsBool() == true)
            {
                continue;
            }

            ids.Add(id);
            reach[id] = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in doctor["reachablePawnIds"].AsSlice())
            {
                var other = target.AsString();
                if (other == id)
                {
                    throw new AcceptanceException($"{id} is listed as reaching itself");
                }

                _ = reach[id].Add(other);
            }
        }

        if (ids.Count < 2)
        {
            throw new AcceptanceException($"expected at least two colonist rows, read {ids.Count}");
        }

        foreach (var a in ids)
        {
            foreach (var b in ids)
            {
                if (a != b && reach[a].Contains(b) != reach[b].Contains(a))
                {
                    throw new AcceptanceException($"asymmetric reachability between {a} and {b}");
                }
            }
        }
    }
}
