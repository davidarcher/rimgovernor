namespace RimGovernor.Brain.Policy;

// Mechanic card evaluation (#3217 Gate 1). A verified count card (#3281) is priced for one pawn and one outfit
// (CardStageOffset): the hand-kept element binding table (ItemBindings) maps the counted element's decl to a typed
// observation of a gear option, and an unmapped decl is unknown. A hand-kept site -> action-kind map (SiteActions) names,
// for a prevention row (DisallowCounted), the existing action that makes the site's count guard false.
// Unknown never certifies. `thoughtaudit -guards` fails when a table row names a decl or site absent from the card file
// or the decompiled game.

/// <summary>A grant site and the existing action kind that, under Constraint, makes its guards false.</summary>
public sealed record CardSiteAction(string Site, ActionKind Action, string Constraint);

/// <summary>What a card's stage offset is evaluated against for one pawn (#3281): its observed trait and hediff def names and whether Anomaly is active.</summary>
public sealed record CardPawnFacts
{
    public Fact<EquatableArray<string>> Traits { get; init; }

    /// <summary>The pawn's hediff def names; known only when the observed list is complete.</summary>
    public Fact<EquatableArray<string>> Hediffs { get; init; }

    /// <summary>While Anomaly is inactive, a hediff only Anomaly adds (<see cref="MechanicCardGuards.AnomalyHediffs"/>) cannot exist.</summary>
    public Fact<bool> AnomalyActive { get; init; }
}

/// <summary>A count guard's element decl bound to the counted item's observed truth (#3281).</summary>
public sealed record CardItemBinding(string Decl, Func<GearOption, Fact<bool>> Read);

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
        new("ThoughtWorker_ApparelThought.CurrentStateInternal", ActionKind.ApparelPolicy, DisallowCounted),
    ];

    /// <summary>The count collections the items of <see cref="CardStageOffset"/> stand for: a pawn's worn apparel, priced as the outfit's gear options.</summary>
    public static readonly IReadOnlyList<string> CountCollections = ["Pawn_ApparelTracker.WornApparel"];

    /// <summary>The element binding table (#3281): <c>GearOption.Tainted</c> is native <c>Apparel.WornByCorpse</c> (NativeGearFacts).</summary>
    public static readonly IReadOnlyList<CardItemBinding> ItemBindings =
    [
        new("Apparel.WornByCorpse", o => Known(o.Tainted)),
    ];

    /// <summary>The nullifying hediffs only the Anomaly expansion adds (their XML entry is MayRequire Anomaly).</summary>
    public static readonly IReadOnlyList<string> AnomalyHediffs = ["Inhumanized"];

    /// <summary>Every card decl the binding tables bind, for the tool check.</summary>
    public static IEnumerable<string> BoundDecls => ItemBindings.Select(b => b.Decl).Concat(CountCollections);

    /// <summary>
    /// The mood offset a count card (#3281) gives a pawn wearing <paramref name="items"/>: the <c>baseMoodEffect</c> of the
    /// stage its one active site returns for the count of items its predicate holds for, the index clamped to the last
    /// defined stage as native <c>ThoughtState.StageIndexFor</c> does. 0 when a nullifying trait or hediff is present or no
    /// site is active (a count of 0); unknown when a binding, an observation or a guard the evaluation needs is missing.
    /// It does not check <see cref="MechanicCard.Live"/>: only a verified card may drive live play (#3217).
    /// </summary>
    public static Fact<double> CardStageOffset(MechanicCard card, CardPawnFacts pawn, IReadOnlyList<GearOption> items, IReadOnlyList<CardItemBinding>? bindings = null)
    {
        var nullified = Nullified(card.Xml, pawn);
        if (nullified.TryGetValue(out var n) && n)
        {
            return Known(0.0);
        }
        int? stage = null;
        foreach (var site in card.Sites)
        {
            if (site.Guards is not [{ Count: { } count } guard] || !CountCollections.Contains(count.Over.Decl ?? ""))
            {
                return Unknown<double>();
            }
            var hits = 0;
            foreach (var item in items)
            {
                if (!Item(count.Where, item, bindings ?? ItemBindings).TryGetValue(out var hit))
                {
                    return Unknown<double>();
                }
                hits += hit ? 1 : 0;
            }
            if (!Counted(guard.Expr, hits).TryGetValue(out var active))
            {
                return Unknown<double>();
            }
            if (!active)
            {
                continue;
            }
            // Two active sites, or a stage expression the evaluator does not model, have no one stage.
            if (stage is not null || Index(count.Stage, hits) is not { } index)
            {
                return Unknown<double>();
            }
            stage = index;
        }
        if (card.Sites.Count == 0)
        {
            return Unknown<double>();
        }
        if (stage is null)
        {
            return Known(0.0);
        }
        if (!nullified.IsKnown)
        {
            return Unknown<double>();
        }
        var at = Math.Min(stage.Value, card.Xml.Stages.Count - 1);
        return Known(at < 0 ? 0.0 : card.Xml.Stages[at].BaseMoodEffect ?? 0);
    }

    /// <summary>Whether a count card counts <paramref name="item"/>: some site's count <c>where</c> holds for it; unknown when a binding is missing.</summary>
    public static Fact<bool> Counts(MechanicCard card, GearOption item, IReadOnlyList<CardItemBinding>? bindings = null)
    {
        var any = false;
        foreach (var count in card.Sites.SelectMany(s => s.Guards).Select(g => g.Count).OfType<MechanicCardCount>())
        {
            if (!Item(count.Where, item, bindings ?? ItemBindings).TryGetValue(out var hit))
            {
                return Unknown<bool>();
            }
            any |= hit;
        }
        return Known(any);
    }

    /// <summary>Whether a nullifying trait or hediff is present; unknown when one could be and is not observed.</summary>
    private static Fact<bool> Nullified(MechanicCardXml xml, CardPawnFacts pawn)
    {
        var unknown = false;
        foreach (var trait in xml.NullifyingTraits)
        {
            if (!pawn.Traits.TryGetValue(out var traits))
            {
                unknown = true;
            }
            else if (traits.Contains(trait))
            {
                return Known(true);
            }
        }
        foreach (var hediff in xml.NullifyingHediffs)
        {
            if (pawn.Hediffs.TryGetValue(out var hediffs))
            {
                if (hediffs.Contains(hediff))
                {
                    return Known(true);
                }
            }
            else if (!(AnomalyHediffs.Contains(hediff) && pawn.AnomalyActive.TryGetValue(out var anomaly) && !anomaly))
            {
                unknown = true;
            }
        }
        return unknown ? Unknown<bool>() : Known(false);
    }

    private static Fact<bool> Item(MechanicCardExpr e, GearOption item, IReadOnlyList<CardItemBinding> bindings)
    {
        var args = e.Args ?? [];
        switch (e.Op)
        {
            case null when e.Decl is { Length: > 0 } decl:
                return bindings.FirstOrDefault(b => b.Decl == decl)?.Read(item) ?? Unknown<bool>();
            case null:
                return e.Lit is "true" or "false" ? Known(e.Lit == "true") : Unknown<bool>();
            case "not" when args.Count == 1:
                return Item(args[0], item, bindings).TryGetValue(out var v) ? Known(!v) : Unknown<bool>();
            case "and" or "or" when args.Count == 2:
                var known = Item(args[0], item, bindings).TryGetValue(out var a) & Item(args[1], item, bindings).TryGetValue(out var b);
                return known ? Known(e.Op == "and" ? a && b : a || b) : Unknown<bool>();
            default:
                return Unknown<bool>();
        }
    }

    /// <summary>A count guard's condition at a count: comparisons of the count symbol and integer literals, under not/and/or.</summary>
    private static Fact<bool> Counted(MechanicCardExpr e, int count)
    {
        var args = e.Args ?? [];
        switch (e.Op)
        {
            case null:
                return e.Lit is "true" or "false" ? Known(e.Lit == "true") : Unknown<bool>();
            case "not" when args.Count == 1:
                return Counted(args[0], count).TryGetValue(out var v) ? Known(!v) : Unknown<bool>();
            case "and" or "or" when args.Count == 2:
                var known = Counted(args[0], count).TryGetValue(out var a) & Counted(args[1], count).TryGetValue(out var b);
                return known ? Known(e.Op == "and" ? a && b : a || b) : Unknown<bool>();
            case "==" or "!=" or "<" or ">" or "<=" or ">=" when args.Count == 2 && Index(args[0], count) is { } l && Index(args[1], count) is { } r:
                return Known(e.Op switch { "==" => l == r, "!=" => l != r, "<" => l < r, ">" => l > r, "<=" => l <= r, _ => l >= r });
            default:
                return Unknown<bool>();
        }
    }

    /// <summary>An integer expression over the count: the count symbol, integer literals, + and -.</summary>
    private static int? Index(MechanicCardExpr e, int count) => e switch
    {
        { Op: null, Kind: "count" } => count,
        { Op: null, Lit: { } lit } => int.TryParse(lit, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var i) ? i : null,
        { Op: "+" or "-", Args: [var l, var r] } => Index(l, count) is { } a && Index(r, count) is { } b ? (e.Op == "+" ? a + b : a - b) : null,
        _ => null,
    };
}
