namespace RimGovernor.Brain.Policy;

/// <summary>The armor a pawn's race gives every body part, the innermost layer of a hit (the race's armor-rating stats).</summary>
public sealed record NaturalArmor
{
    public double Sharp { get; init; }
    public double Blunt { get; init; }
    public double Heat { get; init; }
}

/// <summary>What one hit of a weapon bin does to one pawn in one outfit, averaged over where the hit lands.</summary>
public sealed record ArmorResponse
{
    /// <summary>Damage that gets through, per hit.</summary>
    public double ExpectedDamage { get; init; }

    /// <summary>The chance the hit is a sharp hit that gets through unhalved, so it cuts and bleeds.</summary>
    public double BleedChance { get; init; }

    /// <summary>The chance some layer turns the hit away entirely.</summary>
    public double DeflectChance { get; init; }
}

/// <summary>
/// <c>ArmorUtility.GetPostArmorDamage</c> as an expectation (#3540). For a hit on a part, the garments covering it are tried
/// outermost first, then the race's natural armor. Each layer has an effective armor of max(rating − penetration, 0) and
/// <c>ArmorUtility.ApplyArmor</c> draws one number against it: under half the effective armor the hit is deflected (no
/// damage), under the effective armor it is halved (and a sharp hit becomes blunt, so no layer after it counts a sharp
/// rating and it no longer bleeds), otherwise it passes. A garment's rating is the option's stat × the quality's armor
/// multiplier (<see cref="GearLoadoutRules.GearQualityMultipliers"/>) × its condition, the planner's reading of the stat's quality part
/// and hit-point scaling. The game rounds a halved amount at random; the expectation halves it exactly. The part is drawn
/// from <see cref="BodyCoverage"/>, not from any observed hit record; a part no garment covers takes only natural armor.
/// </summary>
public static class ArmorResponseRules
{
    /// <summary>The response to one <paramref name="bin"/> hit of a pawn in <paramref name="outfit"/>.</summary>
    public static ArmorResponse Of(IReadOnlyList<GearOption> outfit, NaturalArmor natural, BodyCoverage coverage, WeaponBin bin)
    {
        var total = default(Tally);
        foreach (var share in coverage.Shares)
        {
            Add(ref total, outfit, natural, coverage, bin, share.Groups, share.Probability);
        }

        Add(ref total, outfit, natural, coverage, bin, default, coverage.Uncovered);
        return new ArmorResponse { ExpectedDamage = total.Damage, BleedChance = total.Bleed, DeflectChance = total.Deflect };
    }

    /// <summary>The response to each of <paramref name="bins"/> for one outfit: the one array an outfit swap replaces.</summary>
    public static ArmorResponse[] OfOutfit(IReadOnlyList<GearOption> outfit, NaturalArmor natural, BodyCoverage coverage, IReadOnlyList<WeaponBin> bins)
    {
        var responses = new ArmorResponse[bins.Count];
        for (var i = 0; i < responses.Length; i++)
        {
            responses[i] = Of(outfit, natural, coverage, bins[i]);
        }

        return responses;
    }

    private struct Tally
    {
        internal double Damage;
        internal double Bleed;
        internal double Deflect;
    }

    private static void Add(ref Tally total, IReadOnlyList<GearOption> outfit, NaturalArmor natural, BodyCoverage coverage, WeaponBin bin, EquatableArray<string> groups, double probability)
    {
        if (probability <= 0)
        {
            return;
        }

        // Garments covering the part, innermost first by their outermost layer.
        Span<int> order = stackalloc int[outfit.Count];
        Span<int> depth = stackalloc int[outfit.Count];
        var n = 0;
        for (var i = 0; i < outfit.Count; i++)
        {
            var d = Depth(outfit[i], coverage.Layers);
            if (d < 0 || !Covers(outfit[i], groups))
            {
                continue;
            }

            var at = n++;
            while (at > 0 && depth[at - 1] > d)
            {
                order[at] = order[at - 1];
                depth[at] = depth[at - 1];
                at--;
            }

            order[at] = i;
            depth[at] = d;
        }

        // Armor per layer, outermost first, then natural: as the hit's own category, and as blunt once a sharp hit is halved.
        var afterCategory = bin.Category == ArmorCategory.Sharp ? ArmorCategory.Blunt : bin.Category;
        Span<double> first = stackalloc double[n + 1];
        Span<double> afterHalve = stackalloc double[n + 1];
        for (var k = 0; k < n; k++)
        {
            var o = outfit[order[n - 1 - k]];
            var (quality, _) = GearLoadoutRules.GearQualityMultipliers(o.Quality);
            var scale = quality * o.Condition;
            first[k] = Rating(o, bin.Category) * scale;
            afterHalve[k] = Rating(o, afterCategory) * scale;
        }

        first[n] = Rating(natural, bin.Category);
        afterHalve[n] = Rating(natural, afterCategory);
        var tally = default(Tally);
        Walk(first, afterHalve, 0, bin, false, bin.Damage, probability, ref tally);
        total.Damage += tally.Damage;
        total.Bleed += tally.Bleed;
        total.Deflect += tally.Deflect;
    }

    // converted: a sharp hit a layer halved, now blunt.
    private static void Walk(ReadOnlySpan<double> first, ReadOnlySpan<double> afterHalve, int layer, WeaponBin bin, bool converted, double damage, double probability, ref Tally tally)
    {
        if (layer == first.Length)
        {
            tally.Damage += probability * damage;
            if (bin.Category == ArmorCategory.Sharp && !converted)
            {
                tally.Bleed += probability;
            }

            return;
        }

        var effective = Math.Clamp((converted ? afterHalve[layer] : first[layer]) - bin.ArmorPenetration, 0, 1);
        tally.Deflect += probability * effective / 2;
        Walk(first, afterHalve, layer + 1, bin, converted || bin.Category == ArmorCategory.Sharp, damage / 2, probability * effective / 2, ref tally);
        Walk(first, afterHalve, layer + 1, bin, converted, damage, probability * (1 - effective), ref tally);
    }

    private static double Rating(GearOption o, ArmorCategory category) =>
        category switch
        {
            ArmorCategory.Sharp => o.Sharp,
            ArmorCategory.Blunt => o.Blunt,
            _ => o.ArmorHeat,
        };

    private static double Rating(NaturalArmor n, ArmorCategory category) =>
        category switch
        {
            ArmorCategory.Sharp => n.Sharp,
            ArmorCategory.Blunt => n.Blunt,
            _ => n.Heat,
        };

    private static int Depth(GearOption o, EquatableArray<string> layers)
    {
        var depth = -1;
        foreach (var layer in o.Layers)
        {
            for (var i = layers.Count - 1; i > depth; i--)
            {
                if (layers[i] == layer)
                {
                    depth = i;
                    break;
                }
            }
        }

        return depth;
    }

    private static bool Covers(GearOption o, EquatableArray<string> groups)
    {
        foreach (var g in o.Groups)
        {
            foreach (var h in groups)
            {
                if (g == h)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
