using System.Text.Json.Nodes;
using RimGovernor.Brain.NativeFacade;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The healthy-clock fixture precondition (Go <c>clock.go</c>).</summary>
public static class ClockFixture
{
    /// <summary>
    /// Reads the colonists (observations_list_pawns, filter colonist=true) under <paramref name="identity"/> and asserts a
    /// non-empty census with none dead, downed, bleeding, or needing tend: a missing health block is never treated as
    /// healthy. Pawns the colonist filter dropped (animals, prisoners) are expected, not a defect. Returns the colonist count.
    /// </summary>
    public static async Task<int> RequireHealthyColonistsAsync(Harness h, string label, JsonObject identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(h);
        ArgumentNullException.ThrowIfNull(identity);
        var reply = await h.CallAsync(
            label,
            NativeOps.ListPawns,
            new Observations.ListPawnsRequest { Scope = TypedReads.Scope(Harness.IdentityOf(identity)), Filter = new() { Colonist = true } },
            cancellationToken).ConfigureAwait(false);
        var pawns = TypedReads.Observed(label, NativeOps.ListPawns, reply, reply.Outcome?.Observed).Pawns;
        if (pawns.Count == 0)
        {
            throw new AcceptanceException("no colonists returned");
        }

        foreach (var pawn in pawns)
        {
            var blocked = new List<string>();
            if (pawn.Dead)
            {
                blocked.Add("dead");
            }

            if (pawn.Downed)
            {
                blocked.Add("downed");
            }

            if (pawn.Health is not { } health)
            {
                blocked.Add("health");
            }
            else
            {
                if (health.Bleeding)
                {
                    blocked.Add("bleeding");
                }

                if (health.NeedsTend)
                {
                    blocked.Add("needsTend");
                }
            }

            if (blocked.Count > 0)
            {
                throw new AcceptanceException($"healthy-clock fixture prerequisite failed for {pawn.Pawn.Id}: [{string.Join(' ', blocked)}]");
            }
        }

        return pawns.Count;
    }
}
