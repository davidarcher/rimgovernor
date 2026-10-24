using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>What reading a book definition does.</summary>
public enum BookKind
{
    [WireName("")]
    None = 0,

    [WireName("textbook")]
    Textbook,

    [WireName("novel")]
    Novel,

    [WireName("schematic")]
    Schematic,

    /// <summary>An Anomaly book: research at the cost of a mental break.</summary>
    [WireName("tome")]
    Tome,
}

/// <summary>One book ThingDef and its kind.</summary>
public sealed record Book(string Def, BookKind Kind);

/// <summary>One native reading policy: its load id, label and allowed book definitions.</summary>
public sealed record ReadingPolicyEntry
{
    public string Id { get; init; } = "";

    public string Label { get; init; } = "";

    public IReadOnlyList<string> Allowed { get; init; } = [];
}

/// <summary>
/// What one pawn's reading policy owes: the contents to write (null when the policy labelled with its
/// short name already allows exactly them) and the assignment (null when it already holds it).
/// </summary>
public sealed record ReadingPolicyChange
{
    public ReadingPolicy? Write { get; init; }

    public PawnSettings? Assign { get; init; }
}

public static partial class ReadingPolicyRules
{
    /// <summary>
    /// The book definitions pawn should be allowed: a child learning books (textbooks) only; an adult novels
    /// for joy, textbooks when it has a passion to grow or a skill goal to train (<paramref name="learning"/>, #3508)
    /// and schematics when it researches. Anomaly tomes are never allowed. Unknown when the pawn's age is.
    /// </summary>
    public static (IReadOnlyList<string> Defs, bool Known) ReadingBooks(WorkPawn pawn, IReadOnlyList<Book> books, bool learning)
    {
        if (!pawn.Age.TryGetValue(out var age))
        {
            return ([], false);
        }
        var want = new HashSet<BookKind>();
        if (age < HostilityRules.ChildAge)
        {
            want.Add(BookKind.Textbook);
        }
        else
        {
            want.Add(BookKind.Novel);
            if (learning)
            {
                want.Add(BookKind.Textbook);
            }
            if (pawn.Skills.TryGetValue(out var skills))
            {
                foreach (var s in skills)
                {
                    if (!s.Disabled && s.Passion.Length != 0 && s.Passion != "None")
                    {
                        want.Add(BookKind.Textbook);
                    }
                }
            }
            if (pawn.Work.TryGetValue(out var work))
            {
                foreach (var w in work)
                {
                    if (w.Work == WorkType.Research && !w.Disabled && w.Priority > 0)
                    {
                        want.Add(BookKind.Schematic);
                    }
                }
            }
        }
        var defs = books.Where(b => want.Contains(b.Kind)).Select(b => b.Def).Distinct(StringComparer.Ordinal).OrderBy(d => d, StringComparer.Ordinal).ToList();
        return (defs, true);
    }

    /// <summary>
    /// The per-pawn reading policy writes owed: each owned pawn with a reading tracker holds the policy
    /// labelled with its short name, allowing ReadingBooks. A pawn whose short name another owned pawn shares
    /// or that several policies carry waits. <paramref name="training"/> is the player's unmet skill goals.
    /// </summary>
    public static IReadOnlyList<ReadingPolicyChange> ReadingPolicyChanges(IReadOnlyList<WorkPawn> pawns, IReadOnlyList<OwnedName> names, IReadOnlyList<ReadingPolicyEntry> policies, IReadOnlyList<Book> books, IReadOnlyList<SkillGoal> training)
    {
        var learners = training.Select(g => g.Pawn).ToHashSet();
        var shortNames = new Dictionary<PawnId, string>();
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var n in names)
        {
            shortNames[n.PawnId] = n.ShortName;
            var key = n.ShortName.ToLowerInvariant();
            count[key] = count.GetValueOrDefault(key) + 1;
        }
        var output = new List<ReadingPolicyChange>();
        foreach (var pawn in pawns)
        {
            var ok = pawn.PolicyInputs.TryGetValue(out var inputs);
            var name = shortNames.GetValueOrDefault(pawn.Id) ?? "";
            if (!ok || inputs!.ReadingPolicyId is null || name.Length == 0 || count.GetValueOrDefault(name.ToLowerInvariant()) != 1)
            {
                continue;
            }
            var (want, known) = ReadingBooks(pawn, books, learners.Contains(pawn.Id));
            if (!known)
            {
                continue;
            }
            var own = policies.Where(p => p.Label == name).ToList();
            if (own.Count > 1)
            {
                continue;
            }
            ReadingPolicy? write = null;
            PawnSettings? assign = null;
            try
            {
                if (own.Count == 0 || !SortedCopy(own[0].Allowed).SequenceEqual(want, StringComparer.Ordinal))
                {
                    write = new ReadingPolicy(name, want);
                }
                if (own.Count == 0 || inputs!.ReadingPolicyId != own[0].Id)
                {
                    assign = PawnSettings.NewReadingPolicySetting((Domain.PawnId)pawn.Id, name);
                }
            }
            catch (DomainException)
            {
                continue;
            }
            if (write is not null || assign is not null)
            {
                output.Add(new ReadingPolicyChange { Write = write, Assign = assign });
            }
        }
        return output;
    }

    internal static List<string> SortedCopy(IEnumerable<string> v) =>
        v.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
}
