namespace RimGovernor.Brain.Policy;

// Suite priority: the suite queue is served most unhappy first. A claim's pressure is the current summed
// negative mood offset of its pawn's bedroom, space and jealousy thoughts; no history is kept. Thoughts only
// order the queue: which pawns claim a suite and the size each needs stay SuiteClaims' trait targets.
public static partial class PolicyFunctions
{
    /// <summary>
    /// The vanilla ThoughtDef defNames a suite relieves (confirmed against Assembly-CSharp ThoughtDefOf and the
    /// Core/Royalty ThoughtDefs XML).
    /// </summary>
    private static readonly HashSet<string> SuitePressureThoughts = new(StringComparer.Ordinal)
    {
        "SleptInBedroom", // bedroom impressiveness memory (awful/dull stages)
        "SleptInBarracks", // slept in barracks
        "NeedRoomSize", // ThoughtWorker_NeedRoomSize: confined/cramped interior
        "Jealous", // ThoughtWorker_BedroomJealous
        "Greedy", // ThoughtWorker_Greedy: bedroom impressiveness
        "TitleBedroomRequirementsNotMet", // Royalty
        "TitleNoPersonalBedroom", // Royalty
        "SharedBed", // couple made to share a bed (ThoughtDefOf.SharedBed)
    };

    /// <summary>
    /// Each pawn's summed negative offset of the SuitePressureThoughts (0 or less); a pawn whose thoughts are
    /// unknown is left out.
    /// </summary>
    public static Dictionary<PawnId, double> SuitePressure(IReadOnlyList<MoodPawn> pawns)
    {
        var output = new Dictionary<PawnId, double>();
        foreach (var p in pawns)
        {
            if (!p.Thoughts.TryGetValue(out var thoughts))
            {
                continue;
            }
            var sum = 0.0;
            foreach (var t in thoughts)
            {
                if (SuitePressureThoughts.Contains(t.Def) && t.Offset < 0)
                {
                    sum += t.Offset;
                }
            }
            output[p.Id] = sum;
        }
        return output;
    }

    /// <summary>Sorts claims most negative pressure first, pawn id breaking ties.</summary>
    internal static List<SuiteClaim> OrderSuiteClaims(IEnumerable<SuiteClaim> claims, IReadOnlyDictionary<PawnId, double> pressure) =>
        [
            .. claims
                .OrderBy(c => pressure.TryGetValue(c.Pawn, out var p) ? p : 0)
                .ThenBy(c => c.Pawn),
        ];
}
