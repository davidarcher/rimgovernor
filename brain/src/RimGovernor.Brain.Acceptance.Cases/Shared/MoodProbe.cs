using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The read the mechanic-card probes share (#3283, #3328): one situational thought's offset as the brain sees it.</summary>
internal static class MoodProbe
{
    // Situational thoughts refresh on the pawn's needs interval (150 ticks); the read waits for a clean cache.
    private const int Settle = 300;
    private const int SettleMax = 1200;

    /// <summary>
    /// Runs <see cref="Settle"/> ticks at a time until the pawn's situational cache is clean, then returns
    /// the thought's total offset (0 when absent), the raw situational row, the ticks run and the pawn read.
    /// </summary>
    internal static async Task<(double Offset, Thought? Row, int Ticks, PawnState Pawn)> ReadAsync(ITypedSession s, string label, string pawn, string thought, CancellationToken cancellationToken)
    {
        var details = new PawnDetails
        {
            Needs = true, Health = true, Equipment = true, Biography = true, Settings = true, Social = true, Animals = true, Work = true, Schedule = true,
        };
        for (var advanced = 0; ; )
        {
            await s.RunTicksAsync($"settle-{label}", Settle, cancellationToken).ConfigureAwait(false);
            advanced += Settle;
            var read = $"read-{label}-{CaseSupport.Tag(advanced)}";
            var reply = await s.CallAsync(read, NativeOps.ListPawns, new ListPawnsRequest { Scope = s.Scope, Filter = new() { Ids = [pawn] }, Details = details }, cancellationToken).ConfigureAwait(false);
            var observed = TypedReads.Observed(read, NativeOps.ListPawns, reply, reply.Outcome?.Observed);
            var state = observed.Pawns.SingleOrDefault(p => p.Pawn.Id == pawn) ?? throw new AcceptanceException($"{read}: pawn {pawn} missing from the pawn read");
            var social = state.Social ?? throw new AcceptanceException($"{read}: the pawn read carried no social detail");
            if (social.SituationalCacheStale)
            {
                if (advanced >= SettleMax)
                {
                    throw new AcceptanceException($"{label}: the situational thought cache is still stale after {advanced} ticks");
                }

                continue;
            }

            if (!ObservationPackage.MoodThoughts(state).TryGetValue(out var thoughts))
            {
                throw new AcceptanceException($"{label}: the pawn's mood thoughts are unknown");
            }

            return (thoughts.FirstOrDefault(t => t.Def == thought)?.Offset ?? 0, social.Situational.FirstOrDefault(r => r.DefName == thought), advanced, state);
        }
    }

    /// <summary>The card stage whose baseMoodEffect is <paramref name="offset"/>, -1 for none.</summary>
    internal static int StageOf(MechanicCard card, double offset)
    {
        var stages = card.Xml.Stages;
        for (var i = 0; i < stages.Count; i++)
        {
            if (stages[i].BaseMoodEffect == offset)
            {
                return i;
            }
        }

        return -1;
    }
}
