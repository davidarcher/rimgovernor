namespace RimGovernor.Brain.Policy;

// The harm model's composition step (#3542 of #3471): the raid a points budget buys from the factions that can attack, as an
// expected pawn count and a distribution over weapon bins. The catalog decodes the faction defs into a RaidTable; Of reads
// only that table and the points, and an input it lacks makes the composition unknown with a finding, never a default.

/// <summary>One weapon def a raider of a pawn kind can carry, with the def's selection weight and what its rows say of a hit.</summary>
public sealed record RaidWeapon
{
    public string Def { get; init; } = "";

    /// <summary>The def's <c>generateCommonality</c>, the weight among the weapons that share a tag.</summary>
    public double Commonality { get; init; } = 1;

    public WeaponDef Weapon { get; init; } = new();
}

/// <summary>
/// A pawn kind in a faction's combat group: its <c>PawnGenOption.selectionWeight</c> and <c>combatPower</c> (the cost
/// against the raid's points), and every weapon its <c>weaponTags</c> select. A kind with no weapon tags fights with its
/// race's own attacks (a mechanoid's verbs), which the catalog supplies as its one weapon. A tag no weapon def carries is
/// named in <see cref="MissingTags"/>.
/// </summary>
public sealed record RaidKind
{
    public string Def { get; init; } = "";
    public double Weight { get; init; }
    public double CombatPower { get; init; }
    public EquatableArray<RaidWeapon> Weapons { get; init; }
    public EquatableArray<string> MissingTags { get; init; }
}

/// <summary>A <c>PawnGroupMaker</c> of the Combat kind: the group makers compete by <see cref="Commonality"/> among those a raid of the points can use (<see cref="MaxTotalPoints"/> null: no ceiling).</summary>
public sealed record RaidPawnGroup
{
    public double Commonality { get; init; } = 100;
    public double? MaxTotalPoints { get; init; }
    public EquatableArray<RaidKind> Kinds { get; init; }
}

/// <summary>A hostile faction's combat groups and its <c>maxPawnCostPerTotalPointsCurve</c> (empty: no cap on a pawn's cost).</summary>
public sealed record RaidFaction
{
    public string Def { get; init; } = "";
    public EquatableArray<CurvePoint> CostCurve { get; init; }
    public EquatableArray<RaidPawnGroup> Groups { get; init; }
}

/// <summary>The factions the catalog decoded for raids.</summary>
public sealed record RaidTable
{
    public EquatableArray<RaidFaction> Factions { get; init; }
}

/// <summary>
/// The expected raid: <see cref="Pawns"/> and a distribution over <see cref="Bins"/> whose weights sum to 1. Unknown (no
/// pawn count, no bins) when the facts or defs could not support it; <see cref="Findings"/> names each gap.
/// </summary>
public sealed record RaidComposition
{
    public Fact<double> Pawns { get; init; }
    public EquatableArray<WeaponBin> Bins { get; init; }
    public EquatableArray<string> Findings { get; init; }
}

/// <summary>
/// The expected composition of a ground raid (v1: not a sampled group). The hostile factions raid with equal weight. Within
/// a faction the combat groups a raid of these points can use compete by commonality; within a group, a pawn kind is drawn
/// by selection weight among the kinds whose combat power fits the faction's cost curve at these points (the cheapest kind
/// when none does), as <c>PawnGroupMakerUtility.ChoosePawnGenOptionsByPoints</c> draws. A group's pawn count is the points
/// over the weighted mean combat power of those kinds. A kind's weapon is drawn from its tag-matched weapons by commonality.
/// The weapons collapse into bins by armor category, ranged or melee, and damage decile of the whole raid.
/// </summary>
public static class RaidCompositionRules
{
    private const int Deciles = 10;

    /// <summary>The composition of a raid of <paramref name="points"/> from <paramref name="hostileFactions"/> (faction def names).</summary>
    public static RaidComposition Of(double points, IReadOnlyList<string> hostileFactions, RaidTable table)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var unknown = false;
        if (hostileFactions.Count == 0)
        {
            findings.Add("raid_no_hostile_factions");
            unknown = true;
        }

        var factions = new List<RaidFaction>();
        foreach (var def in hostileFactions.Distinct())
        {
            var faction = table.Factions.FirstOrDefault(f => f.Def == def);
            if (faction is null || faction.Groups.Count == 0)
            {
                findings.Add($"raid_faction_no_combat_group:{def}");
                unknown = true;
            }
            else
            {
                factions.Add(faction);
            }
        }

        var entries = new List<(double Weight, WeaponDef Weapon)>();
        var pawns = 0.0;
        if (!unknown)
        {
            foreach (var faction in factions)
            {
                var count = Faction(points, faction, 1.0 / factions.Count, entries, findings);
                unknown |= count <= 0;
                pawns += count / factions.Count;
            }
        }

        var bins = unknown ? [] : Bin(entries);
        if (!unknown && bins.Length == 0)
        {
            findings.Add("raid_no_weapon_bins");
            unknown = true;
        }

        return new RaidComposition
        {
            Pawns = unknown ? default : Known(pawns),
            Bins = unknown ? default : EquatableArray.From(bins),
            Findings = EquatableArray.From(findings),
        };
    }

    // Adds the faction's weapon entries (weights summing to factionWeight) and returns its expected pawn count.
    private static double Faction(double points, RaidFaction faction, double factionWeight, List<(double Weight, WeaponDef Weapon)> entries, SortedSet<string> findings)
    {
        var groups = faction.Groups.Where(g => (g.MaxTotalPoints is not { } ceiling || ceiling >= points) && g.Kinds.Count > 0).ToList();
        if (groups.Count == 0)
        {
            findings.Add($"raid_no_group_for_points:{faction.Def}");
            return 0;
        }

        double? cap = faction.CostCurve.Count == 0 ? null : LayoutFilthRules.EvaluateCurve(faction.CostCurve, points);
        var commonality = groups.Sum(g => g.Commonality);
        var expected = 0.0;
        foreach (var group in groups)
        {
            var share = commonality > 0 ? group.Commonality / commonality : 1.0 / groups.Count;
            var kinds = group.Kinds.Where(k => k.Weight > 0 && k.CombatPower > 0).ToList();
            var fit = kinds.Where(k => cap is not { } limit || k.CombatPower <= limit).ToList();
            if (fit.Count == 0 && kinds.Count > 0)
            {
                fit.Add(kinds.MinBy(k => k.CombatPower)!);
            }

            if (fit.Count == 0)
            {
                findings.Add($"raid_no_group_for_points:{faction.Def}");
                continue;
            }

            var weight = fit.Sum(k => k.Weight);
            var count = points * weight / fit.Sum(k => k.Weight * k.CombatPower);
            expected += share * count;
            foreach (var kind in fit)
            {
                Kind(kind, factionWeight * share * count * kind.Weight / weight, entries, findings);
            }
        }

        return expected;
    }

    private static void Kind(RaidKind kind, double weight, List<(double Weight, WeaponDef Weapon)> entries, SortedSet<string> findings)
    {
        foreach (var tag in kind.MissingTags)
        {
            findings.Add($"raid_weapon_tag_unmatched:{tag}");
        }

        var weapons = new List<RaidWeapon>();
        foreach (var weapon in kind.Weapons)
        {
            if (weapon.Weapon.Damage > 0 && weapon.Weapon.ArmorCategory is not null && weapon.Commonality > 0)
            {
                weapons.Add(weapon);
            }
            else
            {
                findings.Add($"raid_weapon_no_damage:{weapon.Def}");
            }
        }

        if (weapons.Count == 0)
        {
            findings.Add($"raid_kind_no_weapon:{kind.Def}");
            return;
        }

        var total = weapons.Sum(w => w.Commonality);
        foreach (var weapon in weapons)
        {
            entries.Add((weight * weapon.Commonality / total, weapon.Weapon));
        }
    }

    // Groups the entries by (armor category, ranged, damage decile of the whole raid); each bin averages its members by weight.
    private static WeaponBin[] Bin(List<(double Weight, WeaponDef Weapon)> entries)
    {
        var total = entries.Sum(e => e.Weight);
        if (total <= 0)
        {
            return [];
        }

        var below = 0.0;
        var groups = new SortedDictionary<(ArmorCategory Category, bool Ranged, int Decile), List<(double Weight, WeaponDef Weapon)>>();
        foreach (var entry in entries.OrderBy(e => e.Weapon.Damage).ThenBy(e => e.Weapon.AP))
        {
            var decile = Math.Min(Deciles - 1, (int)((below + (entry.Weight / 2)) / total * Deciles));
            below += entry.Weight;
            var key = (entry.Weapon.ArmorCategory!.Value, entry.Weapon.Ranged, decile);
            if (!groups.TryGetValue(key, out var members))
            {
                groups[key] = members = [];
            }

            members.Add(entry);
        }

        return [.. groups.Select(g =>
        {
            var weight = g.Value.Sum(e => e.Weight);
            return new WeaponBin
            {
                Category = g.Key.Category,
                Damage = g.Value.Sum(e => e.Weight * e.Weapon.Damage) / weight,
                ArmorPenetration = g.Value.Sum(e => e.Weight * e.Weapon.AP) / weight,
                Weight = weight / total,
            };
        })];
    }
}
