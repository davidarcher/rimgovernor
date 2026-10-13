namespace RimGovernor.Brain.Policy;

/// <summary>One colonist as the ledger reads it: the observed thought rows plus the attributes immunity and expectation gating need. An unknown attribute never excludes a thought; it marks the source unverified.</summary>
public sealed record MoodLedgerPawn
{
    public PawnId Id { get; init; }
    public Fact<EquatableArray<MoodThought>> Thoughts { get; init; }
    public Fact<EquatableArray<string>> Traits { get; init; }
    public Fact<EquatableArray<string>> Precepts { get; init; }
    public Fact<string> Expectation { get; init; }
}

/// <summary>The mood one pawn loses per thought: negative offsets that apply to it, most negative first. Unknown when its thoughts were unreadable.</summary>
public sealed record MoodPawnLoss
{
    public PawnId Id { get; init; }
    public Fact<EquatableArray<MoodThought>> Lost { get; init; }
}

/// <summary>One thought's colony total: pawns affected x offset.</summary>
public sealed record MoodLedgerSource
{
    public string Def { get; init; } = "";
    public int Pawns { get; init; }
    public double Lost { get; init; }
    public IReadOnlyList<ConcernId> Owners { get; init; } = [];

    /// <summary>Affected pawns whose immunity or expectation gating could not be checked (no ThoughtFacts for the def, or an unknown pawn attribute); their loss is still counted.</summary>
    public int Unverified { get; init; }
}

/// <summary>The expectation level as a visible source: how many pawns sit at each level. Shown only; no concern fixes it.</summary>
public sealed record MoodLedgerExpectation(string Level, int Pawns);

/// <summary>Answers where mood is lost. Sources rank all negative thoughts; Unowned is the subset with no audited owner (empty or unclassified), the next gap-fill chosen by mood lost.</summary>
public sealed record MoodLedger
{
    public IReadOnlyList<MoodPawnLoss> Pawns { get; init; } = [];
    public IReadOnlyList<MoodLedgerSource> Sources { get; init; } = [];
    public IReadOnlyList<MoodLedgerSource> Unowned { get; init; } = [];
    public IReadOnlyList<MoodLedgerExpectation> Expectation { get; init; } = [];

    /// <summary>Pawns whose thoughts were unreadable.</summary>
    public int UnknownPawns { get; init; }
}

/// <summary>Ports <c>mood_ledger.go</c>.</summary>
public static partial class PolicyFunctions
{
    /// <summary>The concerns whose facility removes the thought: the audit table's hand-kept owner column (thought_triggers.tsv), the one place a fix is declared. A def with no row, or an empty owner, has none.</summary>
    internal static IReadOnlyList<ConcernId> ThoughtOwners(string def)
    {
        var (row, ok) = ThoughtAuditRow(def);
        if (!ok || row.Owner == "")
        {
            return [];
        }
        return [.. row.Owner.Split(';').Select(o => new ConcernId(o))];
    }

    /// <summary>Whether the audit table names the concern as the owner of some thought.</summary>
    public static bool MoodProvisionConcern(ConcernId goal)
    {
        foreach (var row in ThoughtAuditRows.Value.Values)
        {
            if (row.Owner.Split(';').Contains(goal.Value))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The vanilla ExpectationDef ladder, lowest first; a ThoughtFacts.MinExpectation or a pawn level outside it is unknown.</summary>
    private static readonly string[] MoodExpectationOrder = ["ExtremelyLow", "VeryLow", "Low", "Moderate", "High", "SkyHigh"];

    /// <summary>Whether the def's facts let the pawn carry the thought; Known is false when a needed pawn attribute is unreadable.</summary>
    private static (bool Applies, bool Known) ThoughtApplies(ThoughtFacts f, MoodLedgerPawn p)
    {
        var known = true;
        var tk = p.Traits.TryGetValue(out var traits);
        var pk = p.Precepts.TryGetValue(out var precepts);
        foreach (var t in f.NullifyingTraits)
        {
            if (!tk)
            {
                known = false;
            }
            else if (traits!.Contains(t))
            {
                return (false, true);
            }
        }
        foreach (var t in f.NullifyingPrecepts)
        {
            if (!pk)
            {
                known = false;
            }
            else if (precepts!.Contains(t))
            {
                return (false, true);
            }
        }
        if (f.RequiredTraits.Count > 0)
        {
            if (!tk)
            {
                known = false;
            }
            else if (!f.RequiredTraits.Any(t => traits!.Contains(t)))
            {
                return (false, true);
            }
        }
        if (f.MinExpectation != "")
        {
            var lk = p.Expectation.TryGetValue(out var level);
            var have = Array.IndexOf(MoodExpectationOrder, level);
            var min = Array.IndexOf(MoodExpectationOrder, f.MinExpectation);
            if (!lk || have < 0 || min < 0)
            {
                known = false;
            }
            else if (have < min)
            {
                return (false, true);
            }
        }
        return (true, known);
    }

    /// <summary>
    /// Turns pawns' thought rows and the defs' ThoughtFacts into per-pawn loss and ranked colony sources. Only
    /// negative offsets are loss. A def with no facts keeps its loss, unverified, and is owned only if the audit
    /// table says so; nothing is dropped or zeroed for missing facts.
    /// </summary>
    public static MoodLedger BuildMoodLedger(IReadOnlyList<MoodLedgerPawn> pawns, IReadOnlyDictionary<string, ThoughtFacts> facts)
    {
        var pawnLosses = new List<MoodPawnLoss>();
        var unknownPawns = 0;
        var sources = new Dictionary<string, MoodLedgerSource>(StringComparer.Ordinal);
        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in pawns)
        {
            if (p.Expectation.TryGetValue(out var level))
            {
                levels[level] = levels.GetValueOrDefault(level) + 1;
            }
            if (!p.Thoughts.TryGetValue(out var rows))
            {
                unknownPawns++;
                pawnLosses.Add(new MoodPawnLoss { Id = p.Id, Lost = Unknown<EquatableArray<MoodThought>>() });
                continue;
            }
            var lost = new List<MoodThought>();
            foreach (var t in rows)
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
            pawnLosses.Add(new MoodPawnLoss { Id = p.Id, Lost = Known(EquatableArray.From(SortThoughts(lost))) });
        }
        var ranked = sources.Values.OrderBy(s => s.Lost).ThenBy(s => s.Def, StringComparer.Ordinal).ToList();
        return new MoodLedger
        {
            Pawns = pawnLosses,
            UnknownPawns = unknownPawns,
            Sources = ranked,
            Unowned = [.. ranked.Where(s => s.Owners.Count == 0)],
            Expectation = [.. levels.Select(kv => new MoodLedgerExpectation(kv.Key, kv.Value)).OrderByDescending(e => e.Pawns).ThenBy(e => e.Level, StringComparer.Ordinal)],
        };
    }

    private static List<MoodThought> SortThoughts(IEnumerable<MoodThought> rows) => [.. rows.OrderBy(t => t.Offset).ThenBy(t => t.Def, StringComparer.Ordinal)];

    /// <summary>The pawn's negative thoughts with no audited owner, most negative first; the ledger's unowned bucket for one pawn.</summary>
    internal static List<MoodThought> MoodUnowned(Fact<EquatableArray<MoodThought>> f)
    {
        if (!f.TryGetValue(out var rows))
        {
            return [];
        }
        return SortThoughts(rows.Where(t => t.Offset < 0 && ThoughtOwners(t.Def).Count == 0));
    }
}
