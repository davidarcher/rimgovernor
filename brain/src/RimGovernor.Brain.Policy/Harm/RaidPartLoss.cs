using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// Where a damage def's hit lands and how it destroys the part (#3847), from which class of its worker declares
/// <c>ChooseHitPart</c> and <c>ApplySpecialEffectsToPart</c>.
/// </summary>
public enum PartHitDraw
{
    /// <summary><c>DamageWorker_AddInjury.ChooseHitPart</c> (and Stab's): any depth for a ranged hit, Outside for a melee one (the melee verb's body region).</summary>
    HitDepth = 0,

    /// <summary>Cut, Blunt, Scratch and Bite: always an Outside part.</summary>
    Outside,

    /// <summary>A worker that is no DamageWorker_AddInjury: the hit adds no injury and destroys no part.</summary>
    NoInjury,

    /// <summary>A DamageWorker_AddInjury subclass that overrides either method with its own: its part loss is not modelled (<c>raid_part_hit_unmodelled:&lt;def&gt;</c>).</summary>
    ForeignWorker,
}

/// <summary>
/// One way a hit's damage reaches the part it lands on (#3847): with <see cref="Chance"/>, the damage times <see cref="Factor"/>, and
/// whether <c>ReduceDamageToPreserveOutsideParts</c> runs on it (an Outside part that is not the core part then survives a hit at or
/// over its health unless the overkill draw destroys it).
/// </summary>
public sealed record PartHitCase(double Chance, double Factor, bool Preserve);

/// <summary>A body part def's <c>GetHitChanceFactorFor</c> a damage def where it is not 1 (0 for a conceptual part).</summary>
public sealed record PartHitFactor(string Part, double Factor);

/// <summary>
/// A damage def's part-loss rule (#3847), the catalog's reading of its worker and def fields: the draw, the cases of the damage on the
/// hit part, the overkill range (<c>overkillPctToDestroyPart</c>), the stab worker's forced internal chance
/// (<c>stabChanceOfForcedInternal</c>) and inner-part share (0.4 of the damage on a part inside an Outside one), and the part defs'
/// hit chance factors.
/// </summary>
public sealed record PartHitRule
{
    public PartHitDraw Draw { get; init; }
    public EquatableArray<PartHitCase> Cases { get; init; }
    public double OverkillMin { get; init; }
    public double OverkillMax { get; init; }
    public double ForcedInternal { get; init; }
    public bool StabSplit { get; init; }
    public EquatableArray<PartHitFactor> HitFactors { get; init; }
}

/// <summary>
/// The catalog's part-loss tables (#3847): the rule of every damage def, and per body part def the tier (<c>PartTier</c>) of each
/// install recipe that replaces it (Recipe_InstallNaturalBodyPart or Recipe_InstallArtificialBodyPart on a fixed part of the def).
/// Empty without a catalog; a damage def or recipe it lacks throws where it is read.
/// </summary>
public sealed record RaidPartCatalog
{
    public IReadOnlyDictionary<string, PartHitRule> Rules { get; init; } = new Dictionary<string, PartHitRule>();
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> Recipes { get; init; } = new Dictionary<string, IReadOnlyDictionary<string, double>>();
}

/// <summary>What a raid's part loss reads besides the pawn (#3847): the outfit worn, the race's armor, the apparel layers innermost first, the catalog's tables and the stock arrival a replacement waits on (#3868).</summary>
public sealed record RaidPartInputs
{
    public EquatableArray<GearOption> Outfit { get; init; }
    public NaturalArmor Natural { get; init; } = new();
    public EquatableArray<string> Layers { get; init; }
    public RaidPartCatalog Catalog { get; init; } = new();
    public StockArrival Stock { get; init; } = new();
}

/// <summary>
/// One part a raid destroys (#3847) with <see cref="Probability"/> a raid: its loss kills (<see cref="Kills"/>), or costs
/// <see cref="Loss"/> capability-days, over the replacement's wait when the autopilot would replace it (<see cref="Pick"/>, otherwise
/// null with the reason) and over the horizon when not.
/// </summary>
internal sealed record PartLossEvent(string Part, double Probability, bool Kills, double Loss, OrganReplacementRead? Pick, SurgeryWantReason Reason);

/// <summary>
/// The parts one raid destroys on one pawn (#3847, epic #3650 P1), from the game's damage rules over the pawn's own parts
/// (<see cref="PawnPartLossFacts"/>). A hit of a weapon bin's damage def lands on a not-missing part drawn by coverageAbs × the part
/// def's hit chance factor (<c>HediffSet.GetRandomNotMissingPart</c>: any depth for a ranged <see cref="PartHitDraw.HitDepth"/> hit,
/// Outside parts otherwise; coverage alone when the factors leave no weight), a stab moved with its forced internal chance to an
/// inside child (<c>DamageWorker_Stab.ChooseHitPart</c>). Its damage passes the armor of the part's groups (<see cref="ArmorResponseRules.Outcomes"/>)
/// and the pawn's IncomingDamageFactor, then each <see cref="PartHitCase"/>: the part is destroyed when the damage reaches its health
/// (<c>HediffSet.AddDirect</c>: part health 0, never the core part nor a part not destroyableByDamage), and for a preserved Outside part
/// only with the overkill chance <c>InverseLerp(min, max, (damage − health) ÷ max health)</c>. Over a raid's Poisson hits at the exposure
/// range's three points, a part with per-hit chance q is lost with 1 − e^(−hits × q). A loss that kills is death unless a colonist's
/// instant-kill protection keeps the part (<see cref="PawnPartLossFacts.InstantKillChance"/>); any other costs its capacity loss to the
/// horizon (the five levels with it missing, <see cref="ForecastValue.CapabilityFactor"/>) unless <see cref="SurgeryRules.RestorePick"/>
/// would book its replacement, which then costs that loss over the expected wait for its part and medicine, E[min(W, horizon)]
/// (<see cref="StockWait"/>, #3868), and is a surgery the hospital credit prices (#3830's rule).
/// </summary>
internal static class RaidPartLoss
{
    // DamageWorker_AddInjury.ShouldReduceDamageToPreservePart and AddDirect never destroy the body's core part.
    // DamageWorker_Stab: 0.4 of the damage lands on a part inside the Outside part it was stabbed through.
    internal const double StabInnerFactor = 0.4;

    /// <summary>The findings every raid part loss names: what the rules above leave out.</summary>
    internal static readonly string[] Unmodelled =
    [
        "raid_part_loss_spread_unmodelled", "raid_part_loss_hit_accumulation_unmodelled", "raid_part_loss_fresh_wound_unmodelled",
        "raid_part_loss_damage_independence_unmodelled",
    ];

    /// <summary>
    /// The parts <paramref name="hits"/> raid hits destroy on <paramref name="pawn"/> (see the class summary), each with its probability a
    /// raid. Throws a RequiredFactException when the pawn's part reads, a bin's damage defs, a damage def's rule or a replacement's tier is unread.
    /// </summary>
    internal static List<PartLossEvent> Of(HitRange hits, IReadOnlyList<WeaponBin> bins, RaidPartInputs inputs, NeedsForecastPawn pawn,
        double[] levels, double movingMin, double incoming, double days, SortedSet<string> findings)
    {
        var read = pawn.PartLoss;
        var parts = read.Parts.ToArray();
        findings.UnionWith(Unmodelled);
        var perHit = PerHit(parts, bins, inputs, incoming, findings);
        var events = new List<PartLossEvent>();
        for (var i = 0; i < parts.Length; i++)
        {
            var lost = PerRaid(hits, perHit[i]);
            if (!(lost > 0))
            {
                continue;
            }

            var part = parts[i];
            if (part.Kills)
            {
                events.Add(new PartLossEvent(part.Part, lost * Math.Clamp(read.InstantKillChance, 0, 1), true, 0, null, SurgeryWantReason.None));
                continue;
            }

            var candidates = read.Replacements.Where(r => r.Part == part.Part)
                .Select(r => (r, inputs.Catalog.Recipes.TryGetValue(part.Part, out var tiers) && tiers.TryGetValue(r.Recipe, out var tier)
                    ? tier : throw new RequiredFactException("organ_replacement_tier:" + r.Recipe)))
                .ToArray();
            var (pick, reason, wait) = SurgeryRules.RestorePick(candidates, inputs.Stock, days);
            StockWait.Name(inputs.Stock, reason, wait, findings);
            double[] after = [part.Consciousness, part.Moving, part.Manipulation, part.Sight, part.Talking];
            var without = wait is { } waiting ? waiting.Expected(days) : days;
            events.Add(new PartLossEvent(part.Part, lost, false, without * (1 - ForecastValue.CapabilityFactor(levels, after, movingMin)), pick, reason));
        }

        return events;
    }

    // Pearson-Tukey over the exposure range, the hits Poisson: the chance some hit destroys the part.
    private static double PerRaid(HitRange hits, double q)
    {
        if (!(q > 0))
        {
            return 0;
        }

        var low = Math.Max(0, hits.Lo);
        var mean = Math.Max(low, hits.Mean);
        var high = Math.Max(mean, hits.Hi);
        return (HarmConsequences.LowWeight * (1 - Math.Exp(-low * q))) + (HarmConsequences.MeanWeight * (1 - Math.Exp(-mean * q)))
            + (HarmConsequences.HighWeight * (1 - Math.Exp(-high * q)));
    }

    // Each part's chance one hit of the mix destroys it.
    private static double[] PerHit(BodyPartLossRead[] parts, IReadOnlyList<WeaponBin> bins, RaidPartInputs inputs, double incoming, SortedSet<string> findings)
    {
        var perHit = new double[parts.Length];
        var total = bins.Sum(b => b.Weight);
        if (!(total > 0))
        {
            return perHit;
        }

        var outcomes = new Dictionary<EquatableArray<string>, List<(double Damage, double Probability)>>();
        var draw = new double[parts.Length];
        foreach (var bin in bins)
        {
            if (bin.Damages.Count == 0)
            {
                throw new RequiredFactException("weapon_bin_damage");
            }

            outcomes.Clear();
            foreach (var damage in bin.Damages)
            {
                var rule = inputs.Catalog.Rules.TryGetValue(damage.Def, out var known) ? known : throw new RequiredFactException("part_hit_rule:" + damage.Def);
                if (rule.Draw == PartHitDraw.NoInjury)
                {
                    continue;
                }

                if (rule.Draw == PartHitDraw.ForeignWorker)
                {
                    findings.Add("raid_part_hit_unmodelled:" + damage.Def);
                    continue;
                }

                Draw(parts, rule, rule.Draw == PartHitDraw.Outside || !bin.Ranged, draw);
                var share = bin.Weight / total * damage.Share;
                for (var i = 0; i < parts.Length; i++)
                {
                    if (!(draw[i] > 0))
                    {
                        continue;
                    }

                    var part = parts[i];
                    if (!outcomes.TryGetValue(part.Groups, out var leaves))
                    {
                        outcomes[part.Groups] = leaves = [];
                        ArmorResponseRules.Outcomes(inputs.Outfit, inputs.Natural, inputs.Layers, bin, part.Groups, leaves);
                    }

                    var factor = incoming * (rule.StabSplit && !part.Outside && StabbedThrough(parts, i) ? StabInnerFactor : 1);
                    var destroyed = 0.0;
                    foreach (var (dealt, probability) in leaves)
                    {
                        destroyed += probability * Destroyed(part, rule, dealt * factor);
                    }

                    perHit[i] += share * draw[i] * destroyed;
                }
            }
        }

        return perHit;
    }

    // DamageWorker_Stab.ApplySpecialEffectsToPart: the part's chain up to its first Outside ancestor is more than the part itself.
    private static bool StabbedThrough(BodyPartLossRead[] parts, int i)
    {
        for (var at = parts[i].Parent; at >= 0; at = parts[at].Parent)
        {
            if (parts[at].Outside)
            {
                return true;
            }
        }

        return false;
    }

    // The chance a hit of `damage` (after armor and the incoming factor) destroys the part it lands on, over the rule's cases.
    private static double Destroyed(BodyPartLossRead part, PartHitRule rule, double damage)
    {
        if (part.Core || !part.Destroyable)
        {
            return 0;
        }

        var destroyed = 0.0;
        foreach (var hit in rule.Cases)
        {
            var dealt = damage * hit.Factor;
            if (!(dealt >= part.Health))
            {
                continue;
            }

            destroyed += hit.Chance * (hit.Preserve && part.Outside ? Overkill(rule, (dealt - part.Health) / Math.Max(part.MaxHealth, 1e-9)) : 1);
        }

        return destroyed;
    }

    // FloatRange.InverseLerpThroughRange (Mathf.InverseLerp): 0 for an empty range.
    private static double Overkill(PartHitRule rule, double over) =>
        rule.OverkillMax != rule.OverkillMin ? Math.Clamp((over - rule.OverkillMin) / (rule.OverkillMax - rule.OverkillMin), 0, 1) : 0;

    // The chance each part is the one a hit of the rule lands on (HediffSet.GetRandomNotMissingPart, then DamageWorker_Stab's forced internal draw).
    private static void Draw(BodyPartLossRead[] parts, PartHitRule rule, bool outside, double[] draw)
    {
        Array.Clear(draw);
        Weigh(parts, rule, p => !outside || parts[p].Outside, draw, 1);
        if (!(rule.ForcedInternal > 0))
        {
            return;
        }

        var drawn = (double[])draw.Clone();
        for (var j = 0; j < parts.Length; j++)
        {
            if (drawn[j] > 0 && parts[j].Outside && Weigh(parts, rule, p => parts[p].Parent == j && !parts[p].Outside, draw, drawn[j] * rule.ForcedInternal))
            {
                draw[j] -= drawn[j] * rule.ForcedInternal;
            }
        }
    }

    // Adds `mass` over the parts `eligible` admits by coverage × hit chance factor (coverage alone when the factors leave none);
    // false when none has coverage.
    private static bool Weigh(BodyPartLossRead[] parts, PartHitRule rule, Func<int, bool> eligible, double[] draw, double mass)
    {
        double weighted = 0, covered = 0;
        for (var p = 0; p < parts.Length; p++)
        {
            if (eligible(p))
            {
                weighted += parts[p].Coverage * Factor(rule, parts[p].Part);
                covered += parts[p].Coverage;
            }
        }

        if (!(covered > 0))
        {
            return false;
        }

        for (var p = 0; p < parts.Length; p++)
        {
            if (eligible(p))
            {
                draw[p] += mass * (weighted > 0 ? parts[p].Coverage * Factor(rule, parts[p].Part) / weighted : parts[p].Coverage / covered);
            }
        }

        return true;
    }

    private static double Factor(PartHitRule rule, string part)
    {
        foreach (var factor in rule.HitFactors)
        {
            if (factor.Part == part)
            {
                return factor.Factor;
            }
        }

        return 1;
    }
}
