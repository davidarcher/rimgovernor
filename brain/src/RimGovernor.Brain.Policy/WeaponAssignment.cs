namespace RimGovernor.Brain.Policy;

// Go: weapon_assignment.go.

/// <summary>Optional: an absent role falls back to skills and traits.</summary>
public enum WeaponRole
{
    [WireName("")]
    None,

    [WireName("melee")]
    Melee,

    [WireName("ranged")]
    Ranged,

    [WireName("sniper")]
    Sniper,

    [WireName("hunter")]
    Hunter,

    [WireName("soldier")]
    Soldier,

    [WireName("non-combatant")]
    NonCombatant,
}

public sealed record EquipAssignment(PawnId Pawn, EquipCandidateWeapon Weapon, double Score);

public static partial class PolicyFunctions
{
    /// <summary>
    /// Scores skill at an accuracy-weighted engagement range, then applies role fit and the known armor census.
    /// Unknown roles/armor are neutral.
    /// </summary>
    public static double ScoreWeapon(EquipCandidatePawn p, EquipCandidateWeapon w)
    {
        if (p.Role == WeaponRole.NonCombatant || p.NoArms.Length != 0)
        {
            return 0;
        }
        if ((w.Biocoded || !w.BiocodedTo.IsEmpty) && w.BiocodedTo != p.Pawn)
        {
            return 0;
        }
        if (!p.IncapableOfViolence.TryGetValue(out var incapable) || incapable)
        {
            return 0;
        }

        // The weapon's def rows: a weapon the producer gave none (default Facts) has no damage and scores nothing.
        var profile = w.Facts;
        if (p.Role == WeaponRole.Hunter && (w.Class != WeaponClass.Ranged || !profile.Hunts()))
        {
            return 0;
        }
        if (profile.ForcedMiss && !p.LoneFighter)
        {
            return 0;
        }
        var shooting = p.Profile.Skills.GetValueOrDefault("Shooting") ?? new ProfileSkill();
        var meleeOnly = p.Profile.Effects.MeleeOnly || p.ShootingDisabled || shooting.Disabled;
        if (w.Class == WeaponClass.Ranged && meleeOnly)
        {
            return 0;
        }
        double level = Math.Max(0, Math.Min(20, shooting.Level));
        var score = profile.DPS;
        if (w.Class == WeaponClass.Ranged)
        {
            // Precision weapons reward the long engagement distance an accurate pawn can exploit; short burst weapons remain useful to novices.
            var accuracy = .35 + (.0325 * level);
            if (profile.Precision)
            {
                accuracy *= accuracy * (1 + (3 * level / 20));
            }
            score *= accuracy * (1 + (profile.Range / 20 * level / 20));
            if (p.Role == WeaponRole.Ranged || p.Role == WeaponRole.Sniper || p.Role == WeaponRole.Hunter)
            {
                score *= 1.3;
            }
            if (p.Role == WeaponRole.Sniper && profile.Precision)
            {
                score *= 1.3;
            }
        }
        else
        {
            level = Math.Max(0, Math.Min(20, (p.Profile.Skills.GetValueOrDefault("Melee") ?? new ProfileSkill()).Level));
            score *= .5 + (level / 40);
            if (meleeOnly || p.Role == WeaponRole.Melee)
            {
                score *= 3;
            }
            else
            {
                score *= .35;
            }
        }
        if (p.RaidArmor.TryGetValue(out var armor) && double.IsFinite(armor))
        {
            score *= 1 - (.75 * Math.Min(1, Math.Max(0, armor - profile.AP)));
        }
        return score;
    }

    /// <summary>The available, fighting-capable unarmed pawns the loose weapons cannot arm: the colonists a weapon must be crafted for.</summary>
    public static int UnarmedFighters(IReadOnlyList<EquipCandidatePawn> pawns, IReadOnlyList<EquipCandidateWeapon> weapons)
    {
        var assigned = AssignEquip(pawns, weapons).Select(pair => pair.Pawn).ToHashSet();
        var n = 0;
        foreach (var p in pawns)
        {
            if (!(p.Armed.TryGetValue(out var armed) && armed) && !assigned.Contains((PawnId)p.Pawn) && EquipAvailable(p))
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>Whether a bench recipe makes a weapon the armory ladder places on a rung.</summary>
    public static bool WeaponRecipe(GearRecipe recipe) => recipe.Armory != ArmoryTier.Unknown;

    private static bool EquipAvailable(EquipCandidatePawn p)
    {
        if (p.NoArms.Length != 0)
        {
            return false;
        }
        foreach (var f in new[] { p.Dead, p.Downed, p.Drafted, p.MentalState, p.IncapableOfViolence })
        {
            if (!f.TryGetValue(out var value) || value)
            {
                return false;
            }
        }
        return p.Armed.TryGetValue(out var armed) && (!armed || p.Current is not null);
    }

    /// <summary>
    /// Greedily assigns all pairs in descending score order, using stable pawn/weapon identities for ties. Each pawn
    /// and thing occurs once. Existing player assignments and biocoded primaries are never displaced.
    /// </summary>
    public static IReadOnlyList<EquipAssignment> AssignEquip(IReadOnlyList<EquipCandidatePawn> pawns, IReadOnlyList<EquipCandidateWeapon> weapons)
    {
        var pairs = new List<(EquipAssignment Assignment, long Distance)>();
        foreach (var p in pawns)
        {
            if (!EquipAvailable(p) || (p.Current is not null && (p.Current.Biocoded || !p.Current.BiocodedTo.IsEmpty)))
            {
                continue;
            }
            var current = 0.0;
            if (p.Current is not null)
            {
                current = ScoreWeapon(p, p.Current);
            }
            foreach (var w in weapons)
            {
                if (w.Thing.Length == 0 || (p.Current is not null && p.Current.Thing == w.Thing))
                {
                    continue;
                }
                var score = ScoreWeapon(p, w);
                if (score <= 0 || (current > 0 && score <= current))
                {
                    continue;
                }
                pairs.Add((new EquipAssignment((PawnId)p.Pawn, w, score), DistanceSquared(p.Position, w.Cell)));
            }
        }
        pairs.Sort((a, b) =>
        {
            if (a.Assignment.Score != b.Assignment.Score)
            {
                return a.Assignment.Score > b.Assignment.Score ? -1 : 1;
            }
            var byPawn = a.Assignment.Pawn.CompareTo(b.Assignment.Pawn);
            if (byPawn != 0)
            {
                return byPawn;
            }
            if (a.Distance != b.Distance)
            {
                return a.Distance < b.Distance ? -1 : 1;
            }
            return string.CompareOrdinal(a.Assignment.Weapon.Thing, b.Assignment.Weapon.Thing);
        });
        var usedPawns = new HashSet<PawnId>();
        var usedWeapons = new HashSet<string>();
        var output = new List<EquipAssignment>();
        foreach (var (pair, _) in pairs)
        {
            if (usedPawns.Contains(pair.Pawn) || usedWeapons.Contains(pair.Weapon.Thing))
            {
                continue;
            }
            usedPawns.Add(pair.Pawn);
            usedWeapons.Add(pair.Weapon.Thing);
            output.Add(pair);
        }
        return output;
    }
}
