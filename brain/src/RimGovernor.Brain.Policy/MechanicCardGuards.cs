namespace RimGovernor.Brain.Policy;

// Mechanic card evaluation (#3217 Gate 1). A verified count card (#3281) is priced for one pawn and one outfit
// (CardStageOffset): the hand-kept element binding table (ItemBindings) maps the counted element's decl to a typed
// observation of a gear option, and an unmapped decl leaves the card unmodeled. A stage card (#3324) is priced at one moment
// (#3327): StageBindings maps its input's decls to the comfort range and ambient temperature, and its ideo decls read the
// pawn's precepts. A hand-kept site -> action-kind map (SiteActions) names, for a prevention row (DisallowCounted), the
// existing action that makes the site's count guard false.
// A card whose shape or decls lie outside what the evaluator models is CardOffset.Unmodeled, which never certifies.
// `thoughtaudit -guards` fails when a table row names a decl or site absent from the card file or the decompiled game.

/// <summary>A grant site and the existing action kind that, under Constraint, makes its guards false.</summary>
public sealed record CardSiteAction(string Site, string Constraint);

/// <summary>
/// What a card's stage offset is evaluated against for one pawn (#3281): its trait and hediff def names, whether Ideology is
/// active (#3327) and the PreceptDef names of the pawn's ideo (empty for a pawn without one).
/// </summary>
public sealed record CardPawnFacts
{
    public required EquatableArray<string> Traits { get; init; }

    public required EquatableArray<string> Hediffs { get; init; }

    /// <summary>ModsConfig.IdeologyActive (#3327).</summary>
    public required bool IdeologyActive { get; init; }

    public required EquatableArray<string> Precepts { get; init; }
}

/// <summary>What a stage card's input reads at one moment (#3327): the pawn's comfortable range, comfort shifts included, and the ambient temperature.</summary>
public sealed record CardStageFacts(double ComfortableMin, double ComfortableMax, double Ambient);

/// <summary>A count guard's element decl bound to the counted item's observed truth (#3281).</summary>
public sealed record CardItemBinding(string Decl, Func<GearOption, bool> Read);

/// <summary>
/// What a card gives a pawn: the <see cref="Staged"/> offset, or <see cref="Unmodeled"/> when the card has no site the
/// evaluator can price, names a decl no binding table row reads, uses an operator outside its grammar or has two active sites.
/// </summary>
public abstract record CardOffset
{
    private CardOffset()
    {
    }

    /// <summary>The mood offset: 0 when a nullifier is present or no site is active.</summary>
    public sealed record Staged(double Offset) : CardOffset;

    /// <summary>The card lies outside the evaluator; it prices nothing and never drives live play.</summary>
    public sealed record Unmodeled : CardOffset;
}

public static class MechanicCardGuards
{
    /// <summary>
    /// The <see cref="CardSiteAction.Constraint"/> of a prevention row (#3284): the action disallows the items the site's
    /// count guard counts (its <c>where</c>), which makes the count 0 and every count guard false.
    /// </summary>
    public const string DisallowCounted = "disallow-counted";

    /// <summary>The site -> action-kind map: the apparel thought's count falls to 0 under a pawn's apparel filter that disallows the counted items.</summary>
    public static readonly IReadOnlyList<CardSiteAction> SiteActions =
    [
        new("ThoughtWorker_ApparelThought.CurrentStateInternal", DisallowCounted),
    ];

    /// <summary>The count collections the items of <see cref="CardStageOffset(MechanicCard, CardPawnFacts, IReadOnlyList{GearOption}, IReadOnlyList{CardItemBinding}?)"/> stand for: a pawn's worn apparel, priced as the outfit's gear options.</summary>
    public static readonly IReadOnlyList<string> CountCollections = ["Pawn_ApparelTracker.WornApparel"];

    /// <summary>The element binding table (#3281): <c>GearOption.Tainted</c> is native <c>Apparel.WornByCorpse</c> (NativeGearFacts).</summary>
    public static readonly IReadOnlyList<CardItemBinding> ItemBindings =
    [
        new("Apparel.WornByCorpse", o => o.Tainted),
    ];

    /// <summary>The stage input binding table (#3327): the numeric decls a stage card's input and guards read.</summary>
    public static readonly IReadOnlyDictionary<string, Func<CardStageFacts, double>> StageBindings = new Dictionary<string, Func<CardStageFacts, double>>(StringComparer.Ordinal)
    {
        ["StatDefOf.ComfyTemperatureMin"] = f => f.ComfortableMin,
        ["StatDefOf.ComfyTemperatureMax"] = f => f.ComfortableMax,
        ["Thing.AmbientTemperature"] = f => f.Ambient,
    };

    /// <summary>The ideo decls a stage modifier reads (#3327), bound to <see cref="CardPawnFacts.IdeologyActive"/> and <see cref="CardPawnFacts.Precepts"/>; a <c>PreceptDefOf</c> field names its precept.</summary>
    private const string IdeologyActive = "ModsConfig.IdeologyActive", PawnIdeo = "Pawn.Ideo", HasPrecept = "Ideo.HasPrecept", PreceptDefOf = "PreceptDefOf.";

    /// <summary>Every card decl the binding tables bind, for the tool check.</summary>
    public static IEnumerable<string> BoundDecls => ItemBindings.Select(b => b.Decl).Concat(CountCollections).Concat(StageBindings.Keys)
        .Concat([IdeologyActive, PawnIdeo, HasPrecept]);

    /// <summary>
    /// The mood offset a stage card (#3324) gives a pawn at <paramref name="at"/> (#3327): when every guard of its one active
    /// site holds, the <c>baseMoodEffect</c> of the band's stage for the input plus the deltas of the modifiers that hold,
    /// clamped to the last defined stage. 0 when a nullifier is present or no site is active (a negative stage is inactive).
    /// <see cref="CardOffset.Unmodeled"/> when a decl is unbound or the card has no banded site or two active ones. It does not
    /// check <see cref="MechanicCard.Live"/>.
    /// </summary>
    public static CardOffset CardStageOffset(MechanicCard card, CardPawnFacts pawn, CardStageFacts at)
    {
        if (Nullified(card.Xml, pawn))
        {
            return new CardOffset.Staged(0);
        }
        if (card.Sites.Count == 0)
        {
            return new CardOffset.Unmodeled();
        }
        int? stage = null;
        foreach (var site in card.Sites)
        {
            if (site.Stage is not { } band || !TryActive(site.Guards, pawn, at, out var on))
            {
                return new CardOffset.Unmodeled();
            }
            if (!on)
            {
                continue;
            }
            if (!TryNumber(band.Input, pawn, at, out var input))
            {
                return new CardOffset.Unmodeled();
            }
            var index = (double)(band.Thresholds.FirstOrDefault(t => input < t.Below)?.Stage ?? band.Else);
            foreach (var modifier in band.Modifiers)
            {
                if (!TryHolds(modifier.When, pawn, at, out var hit))
                {
                    return new CardOffset.Unmodeled();
                }
                index += hit ? modifier.Delta : 0;
            }
            if (index < 0)
            {
                continue;
            }
            if (stage is not null)
            {
                return new CardOffset.Unmodeled();
            }
            stage = (int)index;
        }
        return stage is null ? new CardOffset.Staged(0) : StageOffset(card, stage.Value);
    }

    /// <summary>Whether a stage site's input at <paramref name="at"/> is modeled, and its value; false without a band or with an unbound decl.</summary>
    public static bool TryStageInput(MechanicCardSite site, CardPawnFacts pawn, CardStageFacts at, out double input)
    {
        input = 0;
        return site.Stage is { } band && TryNumber(band.Input, pawn, at, out input);
    }

    /// <summary>The numeric literals a stage card's guards and bands compare against: where its offset can change along its input.</summary>
    public static IReadOnlyList<double> StageCuts(MechanicCard card) => Cuts.GetValue(card, static card =>
    {
        static IEnumerable<double> Literals(MechanicCardExpr e) =>
            (e.Lit is { } lit && double.TryParse(lit.TrimEnd('f', 'F'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? [v] : Array.Empty<double>())
            .Concat((e.Args ?? []).SelectMany(Literals));
        return card.Sites.SelectMany(s => s.Guards.SelectMany(g => Literals(g.Expr))
            .Concat(s.Stage?.Thresholds.Select(t => t.Below) ?? [])).Distinct().ToArray();
    });

    // A card's cuts depend on the card alone, and the coupled search asks for them on every schedule it evaluates.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MechanicCard, double[]> Cuts = new();

    // The evaluator reads a card's expressions through Try functions: false means the expression lies outside the grammar
    // (an unbound decl, an unsupported operator), so the card is Unmodeled; the out value is meaningful only when true.
    // A conjunction with a false conjunct is false, and a disjunction with a true disjunct is true, whatever the other is.

    /// <summary>Whether a site's guards all hold: false is decided by any guard that is false; true needs every guard modeled.</summary>
    private static bool TryActive(IReadOnlyList<MechanicCardGuard> guards, CardPawnFacts pawn, CardStageFacts at, out bool active)
    {
        var modeled = true;
        foreach (var guard in guards)
        {
            if (!TryHolds(guard.Expr, pawn, at, out var holds))
            {
                modeled = false;
            }
            else if (!holds)
            {
                active = false;
                return true;
            }
        }
        active = true;
        return modeled;
    }

    private static bool TryHolds(MechanicCardExpr e, CardPawnFacts pawn, CardStageFacts at, out bool holds)
    {
        var args = e.Args ?? [];
        holds = false;
        switch (e.Op)
        {
            case null when e.Decl == IdeologyActive:
                holds = pawn.IdeologyActive;
                return true;
            case null:
                holds = e.Lit == "true";
                return e.Lit is "true" or "false";
            case "not" when args.Count == 1:
                if (!TryHolds(args[0], pawn, at, out var v))
                {
                    return false;
                }
                holds = !v;
                return true;
            case "and" or "or" when args.Count == 2:
                var left = TryHolds(args[0], pawn, at, out var a);
                var right = TryHolds(args[1], pawn, at, out var b);
                var decided = e.Op == "or";
                if ((left && a == decided) || (right && b == decided))
                {
                    holds = decided;
                    return true;
                }
                holds = !decided;
                return left && right;
            case "==" or "!=" when args is [{ Op: null, Decl: PawnIdeo }, { Lit: "null" }]:
                holds = pawn.Precepts.Count > 0 == (e.Op == "!=");
                return true;
            case "call" when args is [{ Op: null, Decl: HasPrecept }, { Op: null, Decl: { } precept }] && precept.StartsWith(PreceptDefOf, StringComparison.Ordinal):
                holds = pawn.Precepts.Contains(precept[PreceptDefOf.Length..]);
                return true;
            case "==" or "!=" or "<" or ">" or "<=" or ">=" when args.Count == 2:
                if (!TryNumber(args[0], pawn, at, out var l) || !TryNumber(args[1], pawn, at, out var r))
                {
                    return false;
                }
                holds = e.Op switch { "==" => l == r, "!=" => l != r, "<" => l < r, ">" => l > r, "<=" => l <= r, _ => l >= r };
                return true;
            default:
                return false;
        }
    }

    /// <summary>A numeric expression: bound decls, numeric literals, + and -, and conditionals.</summary>
    private static bool TryNumber(MechanicCardExpr e, CardPawnFacts pawn, CardStageFacts at, out double value)
    {
        value = 0;
        if (e is { Op: null, Decl: { } decl } && StageBindings.TryGetValue(decl, out var read))
        {
            value = read(at);
            return true;
        }
        if (e is { Op: null, Lit: { } lit })
        {
            return double.TryParse(lit.TrimEnd('f', 'F'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        }
        if (e is { Op: "+" or "-", Args: [var l, var r] })
        {
            if (!TryNumber(l, pawn, at, out var a) || !TryNumber(r, pawn, at, out var b))
            {
                return false;
            }
            value = e.Op == "+" ? a + b : a - b;
            return true;
        }
        if (e is { Op: "cond", Args: [var c, var then, var otherwise] })
        {
            return TryHolds(c, pawn, at, out var pick) && TryNumber(pick ? then : otherwise, pawn, at, out value);
        }
        return false;
    }

    /// <summary>
    /// The mood offset a count card (#3281) gives a pawn wearing <paramref name="items"/>: the <c>baseMoodEffect</c> of the
    /// stage its one active site returns for the count of items its predicate holds for, the index clamped to the last
    /// defined stage as native <c>ThoughtState.StageIndexFor</c> does. 0 when a nullifying trait or hediff is present or no
    /// site is active (a count of 0); <see cref="CardOffset.Unmodeled"/> when a binding or a guard the evaluation needs lies
    /// outside the tables. It does not check <see cref="MechanicCard.Live"/>: only a verified card may drive live play (#3217).
    /// </summary>
    public static CardOffset CardStageOffset(MechanicCard card, CardPawnFacts pawn, IReadOnlyList<GearOption> items, IReadOnlyList<CardItemBinding>? bindings = null)
    {
        if (Nullified(card.Xml, pawn))
        {
            return new CardOffset.Staged(0);
        }
        int? stage = null;
        foreach (var site in card.Sites)
        {
            if (site.Guards is not [{ Count: { } count } guard] || !CountCollections.Contains(count.Over.Decl ?? ""))
            {
                return new CardOffset.Unmodeled();
            }
            var hits = 0;
            foreach (var item in items)
            {
                if (!TryItem(count.Where, item, bindings ?? ItemBindings, out var hit))
                {
                    return new CardOffset.Unmodeled();
                }
                hits += hit ? 1 : 0;
            }
            if (!TryCounted(guard.Expr, hits, out var active))
            {
                return new CardOffset.Unmodeled();
            }
            if (!active)
            {
                continue;
            }
            // Two active sites, or a stage expression the evaluator does not model, have no one stage.
            if (stage is not null || !TryIndex(count.Stage, hits, out var index))
            {
                return new CardOffset.Unmodeled();
            }
            stage = index;
        }
        if (card.Sites.Count == 0)
        {
            return new CardOffset.Unmodeled();
        }
        return stage is null ? new CardOffset.Staged(0) : StageOffset(card, stage.Value);
    }

    /// <summary>The <c>baseMoodEffect</c> of stage <paramref name="stage"/> clamped to the card's last defined stage; 0 for a card with none.</summary>
    private static CardOffset.Staged StageOffset(MechanicCard card, int stage)
    {
        var last = Math.Min(stage, card.Xml.Stages.Count - 1);
        return new CardOffset.Staged(last < 0 ? 0 : card.Xml.Stages[last].BaseMoodEffect);
    }

    /// <summary>Whether a nullifying trait or hediff is present.</summary>
    private static bool Nullified(MechanicCardXml xml, CardPawnFacts pawn) =>
        xml.NullifyingTraits.Any(t => pawn.Traits.Contains(t)) || xml.NullifyingHediffs.Any(h => pawn.Hediffs.Contains(h));

    private static bool TryItem(MechanicCardExpr e, GearOption item, IReadOnlyList<CardItemBinding> bindings, out bool holds)
    {
        var args = e.Args ?? [];
        holds = false;
        switch (e.Op)
        {
            case null when e.Decl is { Length: > 0 } decl:
                if (bindings.FirstOrDefault(b => b.Decl == decl) is not { } binding)
                {
                    return false;
                }
                holds = binding.Read(item);
                return true;
            case null:
                holds = e.Lit == "true";
                return e.Lit is "true" or "false";
            case "not" when args.Count == 1:
                if (!TryItem(args[0], item, bindings, out var v))
                {
                    return false;
                }
                holds = !v;
                return true;
            case "and" or "or" when args.Count == 2:
                if (!TryItem(args[0], item, bindings, out var a) || !TryItem(args[1], item, bindings, out var b))
                {
                    return false;
                }
                holds = e.Op == "and" ? a && b : a || b;
                return true;
            default:
                return false;
        }
    }

    /// <summary>A count guard's condition at a count: comparisons of the count symbol and integer literals, under not/and/or.</summary>
    private static bool TryCounted(MechanicCardExpr e, int count, out bool holds)
    {
        var args = e.Args ?? [];
        holds = false;
        switch (e.Op)
        {
            case null:
                holds = e.Lit == "true";
                return e.Lit is "true" or "false";
            case "not" when args.Count == 1:
                if (!TryCounted(args[0], count, out var v))
                {
                    return false;
                }
                holds = !v;
                return true;
            case "and" or "or" when args.Count == 2:
                if (!TryCounted(args[0], count, out var a) || !TryCounted(args[1], count, out var b))
                {
                    return false;
                }
                holds = e.Op == "and" ? a && b : a || b;
                return true;
            case "==" or "!=" or "<" or ">" or "<=" or ">=" when args.Count == 2 && TryIndex(args[0], count, out var l) && TryIndex(args[1], count, out var r):
                holds = e.Op switch { "==" => l == r, "!=" => l != r, "<" => l < r, ">" => l > r, "<=" => l <= r, _ => l >= r };
                return true;
            default:
                return false;
        }
    }

    /// <summary>An integer expression over the count: the count symbol, integer literals, + and -.</summary>
    private static bool TryIndex(MechanicCardExpr e, int count, out int value)
    {
        value = 0;
        if (e is { Op: null, Kind: "count" })
        {
            value = count;
            return true;
        }
        if (e is { Op: null, Lit: { } lit })
        {
            return int.TryParse(lit, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out value);
        }
        if (e is { Op: "+" or "-", Args: [var l, var r] })
        {
            if (!TryIndex(l, count, out var a) || !TryIndex(r, count, out var b))
            {
                return false;
            }
            value = e.Op == "+" ? a + b : a - b;
            return true;
        }
        return false;
    }
}
