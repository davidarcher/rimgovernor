namespace RimGovernor.Brain.Policy;

public static partial class PolicyConstants
{
    public static readonly ConcernId ImproveIdeoligion = new("ImproveIdeoligion");
}

/// <summary>
/// Orders concrete restrictions before worst known mood cost, then mandatory obligations. Benefits break
/// ties; no mixed-unit weights.
/// </summary>
public readonly record struct DesignScore(int Restrictions, double MoodCost, int Obligations, double MoodBenefit)
{
    public int Compare(DesignScore t)
    {
        var v = Restrictions.CompareTo(t.Restrictions);
        if (v != 0)
        {
            return v;
        }
        v = MoodCost.CompareTo(t.MoodCost);
        if (v != 0)
        {
            return v;
        }
        v = Obligations.CompareTo(t.Obligations);
        if (v != 0)
        {
            return v;
        }
        return t.MoodBenefit.CompareTo(MoodBenefit);
    }

    internal DesignScore Add(DesignScore t) => new(Restrictions + t.Restrictions, MoodCost + t.MoodCost, Obligations + t.Obligations, MoodBenefit + t.MoodBenefit);
}

public sealed record DesignMeme
{
    public string Name { get; init; } = string.Empty;

    public bool Structure { get; init; }

    public bool Allowed { get; init; }

    public bool InitialFluid { get; init; }

    public bool Supported { get; init; }

    public IReadOnlyList<string> Exclusions { get; init; } = [];

    public IReadOnlyList<IReadOnlyList<string>> RequireOne { get; init; } = [];

    public int Obligations { get; init; }
}

public sealed record DesignPrecept
{
    public string Name { get; init; } = string.Empty;

    public string Issue { get; init; } = string.Empty;

    public bool Default { get; init; }

    public bool Allowed { get; init; }

    public bool Supported { get; init; }

    public IReadOnlyList<string> RequiredMemes { get; init; } = [];

    public IReadOnlyList<string> ConflictingMemes { get; init; } = [];

    public IReadOnlyList<string> Exclusions { get; init; } = [];

    public DesignScore Score { get; init; }

    public IReadOnlyList<string> WorkTypes { get; init; } = [];

    public IReadOnlyList<string> Thoughts { get; init; } = [];

    public double PotentialMoodBenefit { get; init; }
}

public sealed record DesignOptions
{
    public IReadOnlyList<DesignMeme> Memes { get; init; } = [];

    public IReadOnlyList<DesignPrecept> Precepts { get; init; } = [];

    public IReadOnlyList<string> RequiredMemes { get; init; } = [];

    public Fact<DesignNeeds> Needs { get; init; }
}

public sealed record DesignNeeds
{
    public IReadOnlyList<string> WorkTypes { get; init; } = [];

    public Fact<EquatableArray<MoodThought>> Thoughts { get; init; }

    public int Believers { get; init; }
}

public sealed record DesignChoice
{
    public required IdeoligionDesign Design { get; init; }

    public DesignScore Score { get; init; }

    public int Candidates { get; init; }

    public DesignComparison? Comparison { get; init; }
}

public enum DesignDecision
{
    Improves,

    [WireName("no_improvement")]
    Unchanged,
    Ineligible,
    Unsafe,
}

public sealed record DesignComparison
{
    public DesignDecision Decision { get; init; }

    public DesignScore Current { get; init; }

    public DesignScore Candidate { get; init; }

    public bool WorkRelief { get; init; }

    public double MoodRelief { get; init; }

    public double NewMoodCost { get; init; }
}

public static partial class PolicyFunctions
{
    internal static void InspectIdeoligion(RoundsRun c) => c.Owed(ImproveIdeoligion, 3, c.F.ReformOwed);

    /// <summary>Assigned work and mood with complete believer coverage.</summary>
    public static Fact<DesignNeeds> IdeoligionNeeds(IdeoligionFacts ideo, Fact<EquatableArray<WorkPawn>> workers, Fact<EquatableArray<MoodPawn>> moods)
    {
        if (!workers.TryGetValue(out var rows) || ideo.Believers <= 0)
        {
            return Unknown<DesignNeeds>();
        }
        var workTypes = new List<string>();
        var moodKnown = moods.TryGetValue(out var moodRows);

        var thoughts = new List<MoodThought>();
        var believers = 0;
        foreach (var pawn in rows)
        {
            if (!pawn.PolicyInputs.TryGetValue(out var inputs))
            {
                return Unknown<DesignNeeds>();
            }
            if (inputs.Ideo != ideo.IdeoId)
            {
                continue;
            }
            believers++;
            if (!pawn.Work.TryGetValue(out var work))
            {
                return Unknown<DesignNeeds>();
            }
            foreach (var assignment in work)
            {
                var name = Wire.Name(assignment.Work);
                if (assignment.Priority > 0 && !assignment.Disabled && !workTypes.Contains(name))
                {
                    workTypes.Add(name);
                }
            }
            var row = moodRows.FirstOrDefault(r => r.Id == pawn.Id);
            if (row is null)
            {
                moodKnown = false;
                continue;
            }
            if (!row.Thoughts.TryGetValue(out var observed))
            {
                moodKnown = false;
                continue;
            }
            thoughts.AddRange(observed);
        }
        if (believers != ideo.Believers)
        {
            return Unknown<DesignNeeds>();
        }
        workTypes.Sort(StringComparer.Ordinal);
        return Known(new DesignNeeds
        {
            WorkTypes = workTypes,
            Thoughts = moodKnown ? Known(EquatableArray.From(thoughts)) : default,
            Believers = ideo.Believers,
        });
    }

    internal static bool DesignOverlap(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        foreach (var v in a)
        {
            if (b.Contains(v))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Go compared the JSON encodings of two canonical designs to break score ties. The typed equivalent is
    /// the memes, then the precepts, each compared name by name, a longer list ahead of its own prefix (the
    /// closing bracket sorts after the comma), then the fluid flag.
    /// </summary>
    internal static int CompareDesigns(IdeoligionDesign a, IdeoligionDesign b)
    {
        static int Names(IReadOnlyList<string> x, IReadOnlyList<string> y)
        {
            var n = Math.Min(x.Count, y.Count);
            for (var i = 0; i < n; i++)
            {
                var c = string.CompareOrdinal(x[i], y[i]);
                if (c != 0)
                {
                    return c;
                }
            }
            return y.Count.CompareTo(x.Count);
        }
        var v = Names(a.Memes, b.Memes);
        if (v != 0)
        {
            return v;
        }
        v = Names(a.Precepts, b.Precepts);
        if (v != 0)
        {
            return v;
        }
        return a.Fluid.CompareTo(b.Fluid);
    }

    private static bool TryCanonicalDesign(IdeoligionDesign design, out IdeoligionDesign canonical)
    {
        try
        {
            canonical = IdeoligionDesign.CanonicalIdeoligionDesign(design);
            return true;
        }
        catch (DomainException)
        {
            canonical = design;
            return false;
        }
    }

    public static Fact<DesignScore> ScoreIdeoligion(DesignOptions options, IdeoligionDesign design)
    {
        if (!TryCanonicalDesign(design, out _))
        {
            return Unknown<DesignScore>();
        }
        var score = new DesignScore();
        var exclusions = new List<string>();
        int structures = 0, normal = 0;
        foreach (var name in design.Memes)
        {
            var m = options.Memes.FirstOrDefault(x => x.Name == name);
            if (m is null || !m.Supported || !m.Allowed)
            {
                return Unknown<DesignScore>();
            }
            if (m.Structure)
            {
                structures++;
            }
            else
            {
                normal++;
            }
            if (DesignOverlap(exclusions, m.Exclusions))
            {
                return Unknown<DesignScore>();
            }
            exclusions.AddRange(m.Exclusions);
            score = score with { Obligations = score.Obligations + m.Obligations };
            foreach (var group in m.RequireOne)
            {
                if (!DesignOverlap(group, design.Precepts))
                {
                    return Unknown<DesignScore>();
                }
            }
        }
        if (structures != 1 || normal < 1 || normal > 4)
        {
            return Unknown<DesignScore>();
        }
        foreach (var name in options.RequiredMemes)
        {
            if (!design.Memes.Contains(name))
            {
                return Unknown<DesignScore>();
            }
        }
        exclusions = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in design.Precepts)
        {
            var p = options.Precepts.FirstOrDefault(x => x.Name == name);
            if (p is null)
            {
                return Unknown<DesignScore>();
            }
            if (!p.Allowed || !p.Supported || seen.Contains(p.Issue) || DesignOverlap(exclusions, p.Exclusions) || DesignOverlap(design.Memes, p.ConflictingMemes) || (p.RequiredMemes.Count > 0 && !DesignOverlap(design.Memes, p.RequiredMemes)))
            {
                return Unknown<DesignScore>();
            }
            seen.Add(p.Issue);
            exclusions.AddRange(p.Exclusions);
            score = score.Add(p.Score);
        }
        foreach (var p in options.Precepts)
        {
            if (p.Default && !seen.Contains(p.Issue))
            {
                return Unknown<DesignScore>();
            }
        }
        return Known(score);
    }

    /// <summary>
    /// Searches initial fluid designs (one normal meme). The game-sourced initial fluid range is one;
    /// native rechecks every request. A count guard bounds pathological catalogs without returning a
    /// partial best.
    /// </summary>
    public static Fact<DesignChoice> ChooseStartingIdeoligion(DesignOptions options)
    {
        var memes = options.Memes.OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
        DesignChoice? best = null;
        var visits = 0;
        var exhausted = false;
        foreach (var structure in memes)
        {
            if (!structure.Structure || !structure.Allowed || !structure.Supported)
            {
                continue;
            }
            foreach (var normal in memes)
            {
                if (normal.Structure || !normal.Allowed || !normal.Supported || !normal.InitialFluid)
                {
                    continue;
                }
                IReadOnlyList<string> selected = [structure.Name, normal.Name];
                if (DesignOverlap(structure.Exclusions, normal.Exclusions))
                {
                    continue;
                }
                var issues = new Dictionary<string, List<DesignPrecept>>();
                var mandatory = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in options.Precepts)
                {
                    if (p.Default)
                    {
                        mandatory.Add(p.Issue);
                    }
                    if (p.Allowed && p.Supported && !DesignOverlap(selected, p.ConflictingMemes) && (p.RequiredMemes.Count == 0 || DesignOverlap(selected, p.RequiredMemes)))
                    {
                        if (!issues.TryGetValue(p.Issue, out var list))
                        {
                            list = [];
                            issues[p.Issue] = list;
                        }
                        list.Add(p);
                    }
                }
                foreach (var m in new[] { structure, normal })
                {
                    foreach (var group in m.RequireOne)
                    {
                        foreach (var name in group)
                        {
                            foreach (var p in options.Precepts)
                            {
                                if (p.Name == name)
                                {
                                    mandatory.Add(p.Issue);
                                }
                            }
                        }
                    }
                }
                var keys = mandatory.Order(StringComparer.Ordinal).ToList();
                foreach (var key in keys)
                {
                    if (issues.TryGetValue(key, out var list))
                    {
                        issues[key] = [.. list.Order(Comparer<DesignPrecept>.Create((a, b) =>
                        {
                            var c = a.Score.Compare(b.Score);
                            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
                        }))];
                    }
                }
                var lower = new DesignScore[keys.Count + 1];
                var valid = true;
                for (var i = keys.Count - 1; i >= 0; i--)
                {
                    if (!issues.TryGetValue(keys[i], out var ranked) || ranked.Count == 0)
                    {
                        valid = false;
                        break;
                    }
                    lower[i] = lower[i + 1].Add(ranked[0].Score);
                }
                if (!valid)
                {
                    continue;
                }
                void Walk(int i, IReadOnlyList<string> chosen, IReadOnlyList<string> tags, DesignScore score)
                {
                    visits++;
                    if (visits > 100000)
                    {
                        exhausted = true;
                        return;
                    }
                    if (best is not null && score.Add(lower[i]).Compare(best.Score) >= 0)
                    {
                        return;
                    }
                    if (i == keys.Count)
                    {
                        if (!TryCanonicalDesign(new IdeoligionDesign(EquatableArray.From(selected), EquatableArray.From(chosen), true), out var design))
                        {
                            return;
                        }
                        if (!ScoreIdeoligion(options, design).TryGetValue(out var evaluated))
                        {
                            return;
                        }
                        var better = best is null;
                        if (best is not null)
                        {
                            var c = evaluated.Compare(best.Score);
                            better = c < 0 || (c == 0 && CompareDesigns(design, best.Design) < 0);
                        }
                        if (better)
                        {
                            best = new DesignChoice { Design = design, Score = evaluated };
                        }
                        return;
                    }
                    foreach (var p in issues[keys[i]])
                    {
                        if (!DesignOverlap(tags, p.Exclusions))
                        {
                            Walk(i + 1, [.. chosen, p.Name], [.. tags, .. p.Exclusions], score.Add(p.Score));
                            if (exhausted)
                            {
                                return;
                            }
                        }
                    }
                }
                Walk(0, [], [], new DesignScore(0, 0, structure.Obligations + normal.Obligations, 0));
                if (exhausted)
                {
                    return Unknown<DesignChoice>();
                }
            }
        }
        if (best is null)
        {
            return Unknown<DesignChoice>();
        }
        return Known(best with { Candidates = visits });
    }

    /// <summary>
    /// Keeps memes and special instances when their transition cost cannot be valued. Plain-precept changes
    /// may improve known restrictions/mood while preserving obligations and every unvalued effect.
    /// </summary>
    public static Fact<DesignChoice> ChooseIdeoligionReform(DesignOptions options, IdeoligionDesign current, Fact<IdeoDevelopment> development, Fact<bool> calm)
    {
        if (!development.TryGetValue(out var state) || !calm.TryGetValue(out var safe))
        {
            return Unknown<DesignChoice>();
        }
        if (!safe || !state.Fluid || !state.CanReform)
        {
            var decision = DesignDecision.Ineligible;
            if (!safe)
            {
                decision = DesignDecision.Unsafe;
            }
            return Known(new DesignChoice { Design = current, Comparison = new DesignComparison { Decision = decision } });
        }
        if (!options.Needs.TryGetValue(out var needs) || needs.Believers <= 0)
        {
            return Unknown<DesignChoice>();
        }
        if (!ScoreIdeoligion(options, current).TryGetValue(out var baseline))
        {
            return Unknown<DesignChoice>();
        }
        DesignChoice? best = null;
        for (var i = 0; i < current.Precepts.Count; i++)
        {
            var old = current.Precepts[i];
            foreach (var p in options.Precepts)
            {
                if (p.Name == old)
                {
                    continue;
                }
                var oldPrecept = options.Precepts.FirstOrDefault(x => x.Name == old);
                if (oldPrecept is null || p.Issue != oldPrecept.Issue)
                {
                    continue;
                }
                var workImproves = DesignOverlap(oldPrecept.WorkTypes, needs.WorkTypes) && !DesignOverlap(p.WorkTypes, needs.WorkTypes);
                double penalty = 0, benefit = 0;
                var thoughtsKnown = needs.Thoughts.TryGetValue(out var thoughts);
                foreach (var thought in thoughts)
                {
                    if (oldPrecept.Thoughts.Contains(thought.Def) && !p.Thoughts.Contains(thought.Def))
                    {
                        if (thought.Offset < 0)
                        {
                            penalty -= thought.Offset;
                        }
                        else
                        {
                            benefit += thought.Offset;
                        }
                    }
                }
                if (benefit > 0 || (!thoughtsKnown && oldPrecept.Score.MoodBenefit + oldPrecept.PotentialMoodBenefit > 0) || p.PotentialMoodBenefit < oldPrecept.PotentialMoodBenefit)
                {
                    continue;
                }
                var moodImproves = thoughtsKnown && penalty > p.Score.MoodCost * needs.Believers;
                if (!workImproves && !moodImproves)
                {
                    continue;
                }
                // Unknown transition effects are marked unsupported by the catalog adapter.
                var precepts = current.Precepts.ToArray();
                precepts[i] = p.Name;
                var next = new IdeoligionDesign(current.Memes, EquatableArray.From(precepts), true);
                if (!ScoreIdeoligion(options, next).TryGetValue(out var score)
                    || score.Compare(baseline) >= 0 || score.MoodBenefit < baseline.MoodBenefit || score.MoodCost > baseline.MoodCost
                    || score.Obligations > baseline.Obligations || score.Restrictions > baseline.Restrictions)
                {
                    continue;
                }
                if (!TryCanonicalDesign(next, out var canonical))
                {
                    continue;
                }
                if (best is null || score.Compare(best.Score) < 0 || (score.Compare(best.Score) == 0 && CompareDesigns(canonical, best.Design) < 0))
                {
                    best = new DesignChoice
                    {
                        Design = canonical,
                        Score = score,
                        Comparison = new DesignComparison
                        {
                            Decision = DesignDecision.Improves,
                            Current = baseline,
                            Candidate = score,
                            WorkRelief = workImproves,
                            MoodRelief = penalty,
                            NewMoodCost = p.Score.MoodCost * needs.Believers,
                        },
                    };
                }
            }
        }
        if (best is null)
        {
            return Known(new DesignChoice { Design = current, Comparison = new DesignComparison { Decision = DesignDecision.Unchanged, Current = baseline, Candidate = baseline } });
        }
        return Known(best);
    }
}
