namespace RimGovernor.Brain.Policy;

/// <summary>The mood one pawn loses per thought: negative offsets that apply to it, most negative first.</summary>
public sealed record MoodPawnLoss
{
    public required EquatableArray<MoodThought> Lost { get; init; }
}

/// <summary>One thought's colony total: pawns affected x offset.</summary>
public sealed record MoodLedgerSource
{
    public string Def { get; init; } = "";
    public int Pawns { get; init; }
    public double Lost { get; init; }
    public IReadOnlyList<NeedId> Owners { get; init; } = [];

    /// <summary>Affected pawns whose immunity or expectation gating could not be checked (no ThoughtFacts for the def, or an expectation level off the ladder); their loss is still counted.</summary>
    public int Unverified { get; init; }
}

/// <summary>The expectation level as a visible source: how many pawns sit at each level. Shown only; no Need fixes it.</summary>
public sealed record MoodLedgerExpectation(string Level, int Pawns);

/// <summary>Answers where mood is lost. Sources rank all negative thoughts; Unowned is the subset with no audited owner (empty or unclassified), the next gap-fill chosen by mood lost.</summary>
public sealed record MoodLedger
{
    public IReadOnlyList<MoodPawnLoss> Pawns { get; init; } = [];
    public IReadOnlyList<MoodLedgerSource> Sources { get; init; } = [];
    public IReadOnlyList<MoodLedgerSource> Unowned { get; init; } = [];
    public IReadOnlyList<MoodLedgerExpectation> Expectation { get; init; } = [];
}

public static partial class MoodRules
{
    /// <summary>
    /// The Needs whose facility removes the thought: the audit table's hand-kept owner column (thought_triggers.tsv,
    /// NeedIds since #3304), the one place a fix is declared. A def with no row, or an empty owner, has none.
    /// </summary>
    internal static IReadOnlyList<NeedId> ThoughtOwners(string def)
    {
        var (row, ok) = ThoughtFactsRules.ThoughtAuditRow(def);
        if (!ok || row.Owner == "")
        {
            return [];
        }
        return [.. row.Owner.Split(';').Select(o => new NeedId(o))];
    }

    /// <summary>The vanilla ExpectationDef ladder, lowest first; a ThoughtFacts.MinExpectation or a pawn level outside it is unknown.</summary>
    private static readonly string[] MoodExpectationOrder = ["ExtremelyLow", "VeryLow", "Low", "Moderate", "High", "SkyHigh"];

    /// <summary>Whether the def's facts let the pawn carry the thought; Known is false when the pawn's or the def's expectation level is off the ladder.</summary>
    private static (bool Applies, bool Known) ThoughtApplies(ThoughtFacts f, MoodPawn p)
    {
        if (f.NullifyingTraits.Any(name => p.Pawn.Biography.Traits.Any(t => t.DefName == name)) || p.Pawn.Settings.PolicyInputs.Ideo is RimGovernor.Brain.GameFacts.Observations.PawnIdeo.Member member && f.NullifyingPrecepts.Any(member.Precepts.Contains))
        {
            return (false, true);
        }
        if (f.RequiredTraits.Count > 0 && !f.RequiredTraits.Any(name => p.Pawn.Biography.Traits.Any(t => t.DefName == name)))
        {
            return (false, true);
        }
        if (f.MinExpectation == "")
        {
            return (true, true);
        }
        var have = Array.IndexOf(MoodExpectationOrder, p.Pawn.Social.ExpectationDefName);
        var min = Array.IndexOf(MoodExpectationOrder, f.MinExpectation);
        if (have < 0 || min < 0)
        {
            return (true, false);
        }
        return (have >= min, true);
    }

    /// <summary>
    /// Turns pawns' thought rows and the defs' ThoughtFacts into per-pawn loss and ranked colony sources. Only
    /// negative offsets are loss. A def with no facts keeps its loss, unverified, and is owned only if the audit
    /// table says so; nothing is dropped or zeroed for missing facts.
    /// </summary>
    public static MoodLedger BuildMoodLedger(IReadOnlyList<MoodPawn> pawns, IReadOnlyDictionary<string, ThoughtFacts> facts)
    {
        var pawnLosses = new List<MoodPawnLoss>();
        var sources = new Dictionary<string, MoodLedgerSource>(StringComparer.Ordinal);
        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in pawns)
        {
            levels[p.Pawn.Social.ExpectationDefName] = levels.GetValueOrDefault(p.Pawn.Social.ExpectationDefName) + 1;
            var lost = new List<MoodThought>();
            foreach (var t in p.Thoughts)
            {
                if (t.Offset >= 0)
                {
                    continue;
                }
                var verified = false;
                if (facts.TryGetValue(t.Def, out var f))
                {
                    var (applies, k) = ThoughtApplies(f, p);
                    if (!applies)
                    {
                        continue;
                    }
                    verified = k;
                }
                lost.Add(t);
                if (!sources.TryGetValue(t.Def, out var s))
                {
                    s = new MoodLedgerSource { Def = t.Def, Owners = ThoughtOwners(t.Def) };
                }
                sources[t.Def] = s with { Pawns = s.Pawns + 1, Lost = s.Lost + t.Offset, Unverified = s.Unverified + (verified ? 0 : 1) };
            }
            pawnLosses.Add(new MoodPawnLoss { Lost = EquatableArray.From(SortThoughts(lost)) });
        }
        var ranked = sources.Values.OrderBy(s => s.Lost).ThenBy(s => s.Def, StringComparer.Ordinal).ToList();
        return new MoodLedger
        {
            Pawns = pawnLosses,
            Sources = ranked,
            Unowned = [.. ranked.Where(s => s.Owners.Count == 0)],
            Expectation = [.. levels.Select(kv => new MoodLedgerExpectation(kv.Key, kv.Value)).OrderByDescending(e => e.Pawns).ThenBy(e => e.Level, StringComparer.Ordinal)],
        };
    }

    private static List<MoodThought> SortThoughts(IEnumerable<MoodThought> rows) => [.. rows.OrderBy(t => t.Offset).ThenBy(t => t.Def, StringComparer.Ordinal)];

    /// <summary>The pawn's negative thoughts with no audited owner, most negative first; the ledger's unowned bucket for one pawn.</summary>
    internal static List<MoodThought> MoodUnowned(EquatableArray<MoodThought> rows) =>
        SortThoughts(rows.Where(t => t.Offset < 0 && ThoughtOwners(t.Def).Count == 0));
}
