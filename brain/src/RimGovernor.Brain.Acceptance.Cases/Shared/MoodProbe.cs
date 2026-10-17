using System.Text.Json.Nodes;
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

    /// <summary>The thought's total offset (0 when absent) from the mood thoughts the brain lifts out of the pawn read, the raw situational row, the ticks advanced and the decoded pawn read.</summary>
    internal static async Task<(double Offset, JsonObject? Row, int Ticks, PawnState Pawn)> ReadAsync(ISession s, string label, string pawn, string thought, CancellationToken cancellationToken)
    {
        for (var advanced = 0; ; )
        {
            _ = await s.AdvanceAsync(Settle, null, cancellationToken).ConfigureAwait(false);
            advanced += Settle;
            var row = await PawnSupport.ReadPawnAsync(s.Harness, s, $"read-{label}-{CaseSupport.Tag(advanced)}", pawn, true, cancellationToken).ConfigureAwait(false);
            NativeAssert.RequireSocial(row);
            var social = row["social"].AsMap()!;
            if (social["situationalCacheStale"].AsBool() == true)
            {
                if (advanced >= SettleMax)
                {
                    throw new AcceptanceException($"{label}: the situational thought cache is still stale after {advanced} ticks");
                }

                continue;
            }

            var state = WireCodec.ToRecord<PawnState>(row);
            if (!ObservationPackage.MoodThoughts(state).TryGetValue(out var thoughts))
            {
                throw new AcceptanceException($"{label}: the pawn's mood thoughts are unknown: {social.ToJson()}");
            }

            var raw = social["situational"].AsSlice().Select(r => r.AsMap()).FirstOrDefault(r => r?["defName"].AsString() == thought);
            return (thoughts.FirstOrDefault(t => t.Def == thought)?.Offset ?? 0, raw, advanced, state);
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
