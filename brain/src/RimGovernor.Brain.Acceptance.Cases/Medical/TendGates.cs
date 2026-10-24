using RimGovernor.Brain.Bridge;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>medical/tend-gates</c>: the tend table carries the doctor gates NativeTendOperations.Prepare enforces
/// (pawn-control eligibility, WorkGiver_Tend capacities, pairwise reachability), so SelectTend stops proposing doctors
/// the native tend gate refuses (#657).
/// </summary>
internal static class TendGates
{
    internal static TypedCase Case() => new()
    {
        Name = "medical/tend-gates",
        Scope = "the tend table of the lab colonists: a spawned, live, undowned, sane colonist with a drafter is control "
            + "eligible, and reachability names other spawned colonists only, never the row itself, and symmetrically.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var rows = await TypedPawns.ListAsync(s, "tend-gates", new Observations.PawnFilter { Colonist = true }, cancellationToken).ConfigureAwait(false);
        var table = await s.CallAsync(
            "tend-table",
            NativeOps.ReadTendDoctors,
            new Observations.TendDoctorsRequest { Scope = s.Scope, PawnIds = [.. rows.Select(row => row.Pawn.Id ?? "")], ReachBudgetMs = 50 },
            cancellationToken).ConfigureAwait(false);
        var doctors = table.Doctors.ToDictionary(doctor => doctor.PawnId, StringComparer.Ordinal);
        var reach = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var id = row.Pawn.Id ?? "";
            var doctor = doctors.GetValueOrDefault(id) ?? throw new AcceptanceException($"{id} has no tend row; the tend gates are unobserved");
            if (doctor.Spawned && doctor.HasDrafter && !row.Dead && !row.Downed && row.MentalState is null && !doctor.ControlEligible)
            {
                throw new AcceptanceException($"{id} is not control eligible though spawned, live, undowned and sane");
            }

            // An unspawned row reaches nobody and one past the tend budget is skipped; neither takes part in the symmetry check.
            if (!doctor.Spawned)
            {
                continue;
            }

            if (doctor.Reach is not Observations.TendReach.Read read)
            {
                continue;
            }

            if (read.PawnIds.Contains(id))
            {
                throw new AcceptanceException($"{id} is listed as reaching itself");
            }

            reach[id] = [.. read.PawnIds];
        }

        s.Record("reach_checked", reach.Count);
        if (reach.Count < 2)
        {
            throw new AcceptanceException($"expected at least two reach-checked colonists, read {reach.Count}");
        }

        foreach (var (a, from) in reach)
        {
            foreach (var (b, to) in reach)
            {
                if (a != b && from.Contains(b) != to.Contains(a))
                {
                    throw new AcceptanceException($"asymmetric reachability between {a} and {b}");
                }
            }
        }
    }
}
