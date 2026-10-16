namespace RimGovernor.Brain.Policy;

// Guard evaluation for mechanic cards (#3223, #3217 Gate 1). A card site's guards are evaluated against what the brain
// observes, in three classes:
// - an existing thing's state or a target property: a hand-kept symbol binding table (Bindings) maps the card decl to a
//   typed observation; an unmapped symbol is unknown;
// - the state of the container the grant fills (a field of the site's own type with a writer entry on the card): reasoned
//   from the card's writers. A planned fresh instance starts from the field defaults; an existing one reads its prior state
//   through the binding table. A field whose writers have no reset leaves the instance exhausted after one grant;
// - the actor: a hand-kept site -> action-kind map (SiteActions) names the existing action kind that reaches the site and
//   the actor guards it satisfies under a constraint.
// Unknown never certifies: a card is usable only when it is verified and every guard holds or is achievable.
// `thoughtaudit -guards` fails when a table row names a decl or site absent from the card file or the decompiled game.

public enum CardGuardVerdict
{
    Holds,
    Fails,

    /// <summary>An existing action kind makes the guard true; the result names the kind and the actor constraint.</summary>
    Achievable,

    /// <summary>Unbound, unmodelled or unobserved; the result carries the reason.</summary>
    Unknown,
}

/// <summary>One guard's verdict, with the action kind and constraint when achievable and the reason when unknown.</summary>
public sealed record CardGuardResult(MechanicCardGuard Guard, CardGuardVerdict Verdict, ActionKind? Action = null, string Constraint = "", string Reason = "");

/// <summary>What a card's guards are evaluated against: the grant's target thing, the container instance and the colonists.</summary>
public sealed record CardGuardFacts
{
    /// <summary>The thing the grant acts on (the corpse hauled in).</summary>
    public WasteItem Target { get; init; } = new();

    /// <summary>The existing container's thing ID; empty for a planned fresh instance.</summary>
    public string Instance { get; init; } = "";

    /// <summary>The waste census, which shows what an existing container holds.</summary>
    public IReadOnlyList<WasteItem> Waste { get; init; } = [];

    public Fact<EquatableArray<MoodPawn>> Colonists { get; init; }
}

/// <summary>
/// One site's guards evaluated for one target. <see cref="Exhausted"/>: the container serves one grant only (a writer with
/// no reset), so after this grant, or already when its guard fails, the search must propose a fresh instance.
/// </summary>
public sealed record CardEvaluation(MechanicCard Card, MechanicCardSite Site, IReadOnlyList<CardGuardResult> Guards, bool Exhausted)
{
    /// <summary>Verified, and every guard holds or is achievable: no unknown and no failing guard on the path.</summary>
    public bool Usable => Card.Live && Guards.All(g => g.Verdict is CardGuardVerdict.Holds or CardGuardVerdict.Achievable);
}

/// <summary>A card decl bound to its observed truth: a bool's value, a collection's non-emptiness, a reference's non-null.</summary>
public sealed record CardSymbolBinding(string Decl, Func<CardGuardFacts, Fact<bool>> Read);

/// <summary>A grant site reached by an existing action kind, whose actor guards (ActorDecls) that action meets under Constraint.</summary>
public sealed record CardSiteAction(string Site, ActionKind Action, string Constraint, IReadOnlyList<string> ActorDecls);

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
    /// <summary>The symbol binding table: card decls of existing things and target properties, read from typed observations.</summary>
    public static readonly IReadOnlyList<CardSymbolBinding> Bindings =
    [
        new("Corpse.everBuriedInSarcophagus", f => f.Target.CorpseOf == CorpseOf.None ? Unknown<bool>() : Known(f.Target.EverBuriedInSarcophagus)),
        new("RaceProperties.Humanlike", f => f.Target.CorpseOf == CorpseOf.None ? Unknown<bool>() : Known(f.Target.CorpseOf != CorpseOf.Animal)),
        new("MapPawns.FreeColonists", f => f.Colonists.TryGetValue(out var c) ? Known(c.Count > 0) : Unknown<bool>()),
        new("Pawn_NeedsTracker.mood", f => f.Colonists.TryGetValue(out var c) ? Known(c.Any(p => p.Mood.IsKnown)) : Unknown<bool>()),
        // A container holding a body has been non-empty; an empty existing one may have been, which nothing observes.
        new("Building_Sarcophagus.everNonEmpty", f => f.Waste.Any(w => w.State == WasteState.Buried && w.Grave == f.Instance) ? Known(true) : Unknown<bool>()),
    ];

    /// <summary>The site -> action-kind map: a corpse reaches a sarcophagus by a colonist's haul to the tomb stockpile.</summary>
    public static readonly IReadOnlyList<CardSiteAction> SiteActions =
    [
        new("Building_Sarcophagus.Notify_HauledTo", ActionKind.Haul, "colonist", ["Pawn.IsColonist"]),
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
    public static IEnumerable<string> BoundDecls => Bindings.Select(b => b.Decl).Concat(ItemBindings.Select(b => b.Decl)).Concat(CountCollections);

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

    /// <summary>Evaluates every guard of the card's <paramref name="site"/> for one target; the tables default to the hand-kept ones.</summary>
    public static CardEvaluation Evaluate(
        MechanicCard card,
        MechanicCardSite site,
        CardGuardFacts facts,
        IReadOnlyList<CardSymbolBinding>? bindings = null,
        IReadOnlyList<CardSiteAction>? sites = null)
    {
        var context = new Context(site, facts, bindings ?? Bindings, (sites ?? SiteActions).FirstOrDefault(s => s.Site == site.Site));
        var results = new List<CardGuardResult>();
        foreach (var guard in site.Guards)
        {
            if (guard.Expr.Decl is { } decl && context.Action is { } action && action.ActorDecls.Contains(decl))
            {
                results.Add(new CardGuardResult(guard, CardGuardVerdict.Achievable, action.Action, action.Constraint));
                continue;
            }
            var (value, reason) = context.Eval(guard.Expr);
            results.Add(value.TryGetValue(out var v)
                ? new CardGuardResult(guard, v ? CardGuardVerdict.Holds : CardGuardVerdict.Fails)
                : new CardGuardResult(guard, CardGuardVerdict.Unknown, Reason: reason));
        }
        return new CardEvaluation(card, site, results, context.Exhausted);
    }

    private sealed class Context(MechanicCardSite site, CardGuardFacts facts, IReadOnlyList<CardSymbolBinding> bindings, CardSiteAction? action)
    {
        private readonly string owner = site.Site[..Math.Max(site.Site.LastIndexOf('.'), 0)] + ".";

        public CardSiteAction? Action => action;

        public bool Exhausted { get; private set; }

        public (Fact<bool> Value, string Reason) Eval(MechanicCardExpr e)
        {
            if (e.Unknown is { } why)
            {
                return (Unknown<bool>(), why + ": " + e.Text);
            }
            if (e.Op is null)
            {
                return e.Decl is { Length: > 0 } decl ? Symbol(decl) : (Unknown<bool>(), "unbound symbol " + (e.Sym ?? e.Lit));
            }
            var args = e.Args ?? [];
            switch (e.Op)
            {
                case "not" or "nonempty" when args.Count == 1:
                    var (v, r) = Eval(args[0]);
                    return (e.Op == "not" ? Not(v) : v, r);
                case "!=" or "==" when args.Count == 2 && args[1].Lit == "null":
                    var (n, nr) = Eval(args[0]);
                    return (e.Op == "==" ? Not(n) : n, nr);
                case "and" or "or" when args.Count == 2:
                    var (a, ar) = Eval(args[0]);
                    var (b, br) = Eval(args[1]);
                    if (!a.TryGetValue(out var av) || !b.TryGetValue(out var bv))
                    {
                        return (Unknown<bool>(), a.IsKnown ? br : ar);
                    }
                    return (Known(e.Op == "and" ? av && bv : av || bv), "");
                default:
                    return (Unknown<bool>(), "operator " + e.Op + " is not modelled");
            }
        }

        private static Fact<bool> Not(Fact<bool> v) => v.TryGetValue(out var b) ? Known(!b) : v;

        private (Fact<bool>, string) Symbol(string decl)
        {
            if (decl.StartsWith(owner, StringComparison.Ordinal) && site.Writers.FirstOrDefault(w => w.Field == decl) is { } field)
            {
                return Instance(field);
            }
            var row = bindings.FirstOrDefault(b => b.Decl == decl);
            if (row is null)
            {
                return (Unknown<bool>(), "no binding for " + decl);
            }
            var value = row.Read(facts);
            return (value, value.IsKnown ? "" : decl + " is not observed");
        }

        /// <summary>
        /// The container field at the grant: its one set writer's value (a literal, or a follows field negated or not),
        /// over the follows field's prior state, which is the default (false) on a fresh instance and observed on an
        /// existing one. A follows field set to a constant with no reset makes the instance exhausted when that constant
        /// fails the guard.
        /// </summary>
        private (Fact<bool>, string) Instance(MechanicCardField field)
        {
            var sets = field.Writers.Where(w => w.Write == "set").ToList();
            if (sets.Count != 1 || field.WritersFound != field.Writers.Count || field.Writers.Any(w => w.Write is not ("set" or "serialization")))
            {
                return (Unknown<bool>(), field.Field + " writers are not modelled");
            }
            var value = sets[0].Value;
            var negate = value.StartsWith('!');
            var name = value.TrimStart('!');
            if (name is "true" or "false")
            {
                return (Known((name == "true") != negate), "");
            }
            var follow = field.Follows.FirstOrDefault(f => f.Field.EndsWith("." + name, StringComparison.Ordinal));
            var post = follow?.Writers.Where(w => w.Write == "set").Select(w => w.Value).ToList();
            if (follow is null || post is not [var after and ("true" or "false")] || follow.WritersFound != follow.Writers.Count)
            {
                return (Unknown<bool>(), field.Field + " follows " + name + ", which is not modelled");
            }
            Exhausted |= field.ResetWriters == 0 && follow.ResetWriters == 0 && (after == "true") == negate;
            if (facts.Instance.Length == 0)
            {
                return (Known(negate), "");
            }
            var (prior, reason) = Symbol(follow.Field);
            return (prior.TryGetValue(out var p) ? Known(p != negate) : prior, reason);
        }
    }
}
