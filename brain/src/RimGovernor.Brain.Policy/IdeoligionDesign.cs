namespace RimGovernor.Brain.Policy;

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

    /// <summary>The ThingDef names the game offers for veneration (animals, neither insects nor dryads).</summary>
    public IReadOnlyList<string> VeneratedAnimals { get; init; } = [];

    /// <summary>The most animals an ideoligion may venerate.</summary>
    public int MaxVeneratedAnimals { get; init; }

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

    public DesignComparison? Comparison { get; init; }

    /// <summary>The goal (#3500) whose step this reform is; null for a reform no goal asked for.</summary>
    public int? Goal { get; init; }

    /// <summary>Each unmet goal with no reachable step, by goal id, with the first missing prerequisite.</summary>
    public IReadOnlyDictionary<int, string> Blocked { get; init; } = new Dictionary<int, string>();
}

/// <summary>What an ideoligion goal asks the ideoligion to hold.</summary>
public enum IdeoGoalKind
{
    Meme,
    Precept,

    /// <summary>A venerated animal: a ThingDef name, held as an animal precept.</summary>
    Animal,
}

/// <summary>An ideoligion goal the reform plans toward: a meme, a plain precept or a venerated animal the ideoligion should hold.</summary>
public readonly record struct IdeoGoalTarget(int Goal, IdeoGoalKind Kind, string Def);

public enum DesignDecision
{
    Improves,

    [WireName("no_improvement")]
    Unchanged,
    Ineligible,
    Unsafe,

    /// <summary>A goal's next step is chosen and waits for development points.</summary>
    Awaiting,
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

public static class IdeoligionDesignRules
{
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
            if (inputs.Ideo is not PawnIdeo.Member { IdeoId: var believed } || believed != ideo.IdeoId)
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
    /// Score ties between two canonical designs break by
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
    /// The design a new colony starts with. Without a meme target it searches initial fluid designs (one normal meme;
    /// the game-sourced initial fluid range is one). A theme's <paramref name="targets"/> (#3511) are held all at once:
    /// its meme targets, with the faction's required memes, are the memes; each precept target is its issue's precept;
    /// the animal targets are the venerated animals. A design with one initial-fluid normal meme is fluid and one with
    /// more is fixed, since the game's initial fluid ideoligion holds one normal meme
    /// (<c>IdeoFoundation.MemeCountRangeFluidAbsolute</c>). Unknown when no supported legal design holds every target;
    /// native rechecks every request. A count guard bounds pathological catalogs without returning a partial best.
    /// </summary>
    public static Fact<DesignChoice> ChooseStartingIdeoligion(DesignOptions options, IReadOnlyList<IdeoGoalTarget>? targets = null)
    {
        targets ??= [];
        var memes = options.Memes.OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
        List<string> Targeted(IdeoGoalKind kind) => [.. targets.Where(t => t.Kind == kind).Select(t => t.Def).Distinct().Order(StringComparer.Ordinal)];
        var memeTargets = Targeted(IdeoGoalKind.Meme);
        var animals = Targeted(IdeoGoalKind.Animal);
        if (animals.Count > options.MaxVeneratedAnimals || animals.Any(a => !options.VeneratedAnimals.Contains(a))
            || memeTargets.Any(n => !memes.Any(m => m.Name == n && m.Allowed && m.Supported)))
        {
            return Unknown<DesignChoice>();
        }
        var pinned = new Dictionary<string, DesignPrecept>(StringComparer.Ordinal);
        foreach (var name in Targeted(IdeoGoalKind.Precept))
        {
            var p = options.Precepts.FirstOrDefault(x => x.Name == name);
            if (p is null || !p.Allowed || !p.Supported || !pinned.TryAdd(p.Issue, p))
            {
                return Unknown<DesignChoice>();
            }
        }
        var structureTargets = memes.Where(m => m.Structure && memeTargets.Contains(m.Name)).ToList();
        var normalTargets = memeTargets.Where(n => !structureTargets.Any(m => m.Name == n)).ToList();
        if (structureTargets.Count > 1)
        {
            return Unknown<DesignChoice>();
        }
        IReadOnlyList<IReadOnlyList<DesignMeme>> normalSets = normalTargets.Count == 0
            ? [.. memes.Where(m => !m.Structure && m.Allowed && m.Supported && m.InitialFluid).Select(m => (IReadOnlyList<DesignMeme>)[m])]
            : [[.. memes.Where(m => !m.Structure && (normalTargets.Contains(m.Name) || options.RequiredMemes.Contains(m.Name)))]];
        DesignChoice? best = null;
        var visits = 0;
        var exhausted = false;
        foreach (var structure in memes)
        {
            if (!structure.Structure || !structure.Allowed || !structure.Supported || (structureTargets.Count == 1 && structureTargets[0] != structure))
            {
                continue;
            }
            foreach (var normals in normalSets)
            {
                List<DesignMeme> chosenMemes = [structure, .. normals];
                IReadOnlyList<string> selected = [.. chosenMemes.Select(m => m.Name)];
                var memeTags = new List<string>();
                var clash = false;
                foreach (var m in chosenMemes)
                {
                    clash |= DesignOverlap(memeTags, m.Exclusions);
                    memeTags.AddRange(m.Exclusions);
                }
                if (clash)
                {
                    continue;
                }
                var fluid = normals is [{ InitialFluid: true }];
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
                // A pinned precept is its issue's only choice, and the design is impossible when these memes forbid it.
                foreach (var (issue, p) in pinned)
                {
                    mandatory.Add(issue);
                    issues[issue] = issues.TryGetValue(issue, out var list) && list.Contains(p) ? [p] : [];
                }
                foreach (var m in chosenMemes)
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
                        if (!TryCanonicalDesign(new IdeoligionDesign(EquatableArray.From(selected), EquatableArray.From(chosen), fluid, EquatableArray.From(animals)), out var design))
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
                Walk(0, [], [], new DesignScore(0, 0, chosenMemes.Sum(m => m.Obligations), 0));
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
        return Known(best);
    }

    private readonly record struct GoalStep(int Goal, IdeoligionDesign Design);

    /// <summary>
    /// The goals' next reform (#3500): the step of the first goal, by id, whose target the ideoligion does not hold and
    /// that has a reachable one, and the first missing prerequisite of each goal with none. A precept whose meme is
    /// missing steps by adding that meme; a precept conflicting with a held meme steps by removing it; otherwise it
    /// replaces the held precept of its issue. A step is a design the catalog scores, so faction compatibility, meme
    /// counts and meme requirements are the options' own; a step costs what the goal asked for and is not weighed
    /// against the score.
    /// </summary>
    private static (GoalStep? Step, IReadOnlyDictionary<int, string> Blocked) PlanGoals(DesignOptions options, IdeoligionDesign current, IReadOnlyList<IdeoGoalTarget> targets)
    {
        var blocked = new SortedDictionary<int, string>();
        GoalStep? step = null;
        var wanted = targets.Where(t => t.Kind == IdeoGoalKind.Precept).Select(t => t.Def).ToList();
        foreach (var target in targets.OrderBy(t => t.Goal))
        {
            var held = target.Kind switch
            {
                IdeoGoalKind.Meme => current.Memes.Contains(target.Def),
                IdeoGoalKind.Precept => current.Precepts.Contains(target.Def),
                _ => current.VeneratedAnimals.Contains(target.Def),
            };
            if (held)
            {
                continue;
            }
            var (design, reason) = target.Kind switch
            {
                IdeoGoalKind.Meme => AddMeme(options, current, target.Def, wanted),
                IdeoGoalKind.Precept => StepToPrecept(options, current, target.Def, targets),
                _ => AddVeneratedAnimal(options, current, target.Def),
            };
            if (design is null)
            {
                blocked[target.Goal] = reason;
            }
            else
            {
                step ??= new GoalStep(target.Goal, design);
            }
        }
        return (step, blocked);
    }

    /// <summary>The design venerating one more animal: only one the game offers, within the animal limit.</summary>
    private static (IdeoligionDesign? Design, string Reason) AddVeneratedAnimal(DesignOptions options, IdeoligionDesign current, string def)
    {
        var reason = "venerated_animal_unavailable:" + def;
        if (!options.VeneratedAnimals.Contains(def) || current.VeneratedAnimals.Count >= options.MaxVeneratedAnimals)
        {
            return (null, reason);
        }
        return Validated(options, current with { VeneratedAnimals = EquatableArray.From([.. current.VeneratedAnimals, def]) }, reason);
    }

    private static (IdeoligionDesign? Design, string Reason) StepToPrecept(DesignOptions options, IdeoligionDesign current, string def, IReadOnlyList<IdeoGoalTarget> targets)
    {
        var precept = options.Precepts.FirstOrDefault(p => p.Name == def);
        if (precept is null || !precept.Allowed || !precept.Supported)
        {
            return (null, "precept_unavailable:" + def);
        }
        if (precept.RequiredMemes.Count > 0 && !DesignOverlap(current.Memes, precept.RequiredMemes))
        {
            var meme = precept.RequiredMemes.Order(StringComparer.Ordinal).FirstOrDefault(m => options.Memes.Any(x => x.Name == m && x.Allowed && x.Supported));
            return meme is null
                ? (null, "precept_requires_meme:" + precept.RequiredMemes.Order(StringComparer.Ordinal).First())
                : AddMeme(options, current, meme, targets.Where(t => t.Kind == IdeoGoalKind.Precept).Select(t => t.Def).ToList());
        }
        if (DesignOverlap(current.Memes, precept.ConflictingMemes))
        {
            var meme = precept.ConflictingMemes.Order(StringComparer.Ordinal).First(current.Memes.Contains);
            if (targets.Any(t => t.Kind == IdeoGoalKind.Meme && t.Def == meme) || options.RequiredMemes.Contains(meme) || options.Memes.Any(m => m.Name == meme && m.Structure))
            {
                return (null, "precept_conflicts_meme:" + meme);
            }
            var removed = current with { Memes = EquatableArray.From(current.Memes.Where(m => m != meme)) };
            return Validated(options, removed, "meme_removal_invalid:" + meme);
        }
        var precepts = current.Precepts.ToList();
        var old = precepts.FirstOrDefault(p => options.Precepts.Any(x => x.Name == p && x.Issue == precept.Issue));
        if (old is not null)
        {
            if (targets.Any(t => t.Kind == IdeoGoalKind.Precept && t.Def == old))
            {
                return (null, "precept_conflicts_goal:" + old);
            }
            precepts.Remove(old);
        }
        precepts.Add(def);
        return Validated(options, current with { Precepts = EquatableArray.From(precepts) }, "precept_incompatible:" + def);
    }

    /// <summary>The design adding <paramref name="name"/>, with the precept each of its requirements still lacks, preferring one a goal wants.</summary>
    private static (IdeoligionDesign? Design, string Reason) AddMeme(DesignOptions options, IdeoligionDesign current, string name, IReadOnlyList<string> wanted)
    {
        var meme = options.Memes.FirstOrDefault(m => m.Name == name);
        if (meme is null || !meme.Allowed || !meme.Supported)
        {
            return (null, "meme_unavailable:" + name);
        }
        var precepts = current.Precepts.ToList();
        foreach (var group in meme.RequireOne)
        {
            if (DesignOverlap(group, precepts))
            {
                continue;
            }
            var pick = group
                .OrderBy(p => wanted.Contains(p) ? 0 : 1).ThenBy(p => p, StringComparer.Ordinal)
                .FirstOrDefault(p => options.Precepts.Any(x => x.Name == p && x.Allowed && x.Supported));
            var picked = options.Precepts.FirstOrDefault(x => x.Name == pick);
            if (picked is null)
            {
                return (null, "meme_requires_precept:" + name);
            }
            precepts.RemoveAll(p => options.Precepts.Any(x => x.Name == p && x.Issue == picked.Issue));
            precepts.Add(picked.Name);
        }
        return Validated(options, current with { Memes = EquatableArray.From([.. current.Memes, name]), Precepts = EquatableArray.From(precepts) }, "meme_incompatible:" + name);
    }

    private static (IdeoligionDesign? Design, string Reason) Validated(DesignOptions options, IdeoligionDesign design, string reason) =>
        TryCanonicalDesign(design, out var canonical) && ScoreIdeoligion(options, canonical).TryGetValue(out _) ? (canonical, "") : (null, reason);

    /// <summary>
    /// Keeps memes and special instances when their transition cost cannot be valued. Plain-precept changes
    /// may improve known restrictions/mood while preserving obligations and every unvalued effect.
    /// </summary>
    public static Fact<DesignChoice> ChooseIdeoligionReform(DesignOptions options, IdeoligionDesign current, Fact<IdeoDevelopment> development, Fact<bool> calm, IReadOnlyList<IdeoGoalTarget> targets)
    {
        if (!development.TryGetValue(out var state) || !calm.TryGetValue(out var safe))
        {
            return Unknown<DesignChoice>();
        }
        if (!safe || !state.Fluid)
        {
            return Known(new DesignChoice { Design = current, Comparison = new DesignComparison { Decision = safe ? DesignDecision.Ineligible : DesignDecision.Unsafe } });
        }
        var (step, blocked) = PlanGoals(options, current, targets);
        if (step is { } next)
        {
            var baseline = ScoreIdeoligion(options, current).TryGetValue(out var held) ? held : default;
            var candidate = ScoreIdeoligion(options, next.Design).TryGetValue(out var scored) ? scored : default;
            return Known(new DesignChoice
            {
                Design = next.Design,
                Score = candidate,
                Goal = next.Goal,
                Blocked = blocked,
                Comparison = new DesignComparison
                {
                    Decision = state.CanReform ? DesignDecision.Improves : DesignDecision.Awaiting,
                    Current = baseline,
                    Candidate = candidate,
                },
            });
        }
        var choice = state.CanReform
            ? ChooseImprovement(options, current)
            : Known(new DesignChoice { Design = current, Comparison = new DesignComparison { Decision = DesignDecision.Ineligible } });
        if (choice.TryGetValue(out var chosen))
        {
            return Known(chosen with { Blocked = blocked });
        }
        // The goals' blockers stand whether or not the improvement search could read the believers' needs.
        return blocked.Count == 0
            ? choice
            : Known(new DesignChoice { Design = current, Blocked = blocked, Comparison = new DesignComparison { Decision = DesignDecision.Unchanged } });
    }

    private static Fact<DesignChoice> ChooseImprovement(DesignOptions options, IdeoligionDesign current)
    {
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
                var next = current with { Precepts = EquatableArray.From(precepts) };
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
