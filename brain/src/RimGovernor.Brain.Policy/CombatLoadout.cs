namespace RimGovernor.Brain.Policy;

// Go: combat_loadout.go
//
// Types awaiting later slices: EquipCandidatePawn, EquipCandidateWeapon (equip scoring), WeaponRole, WeaponClass,
// ScoreWeapon. Property names on them are the Go field names in PascalCase.

/// <summary>The threat a fight's loadout answers.</summary>
public enum LoadoutThreat
{
    [WireName("")]
    None,

    /// <summary>Drop pods inside the base; close range, high DPS.</summary>
    [WireName("pods")]
    Pods,

    /// <summary>Tribal raiders; out-range their bows with 32+ tiles.</summary>
    [WireName("tribal")]
    Tribal,

    /// <summary>Mechs or shielded melee; one EMP carrier per squad.</summary>
    [WireName("emp")]
    Emp,

    /// <summary>Wardens take blunt weapons to down, not kill.</summary>
    [WireName("prison_break")]
    PrisonBreak,
}

/// <summary>One defender the loadout may re-equip.</summary>
public sealed record LoadoutDefender
{
    public required EquipCandidatePawn Candidate { get; init; }

    /// <summary>Groups defenders for the one-EMP-carrier rule; empty is one squad.</summary>
    public string Squad { get; init; } = "";

    /// <summary>A pawn with Warden work enabled (the prison-break lane).</summary>
    public bool Warden { get; init; }

    /// <summary>The equipped primary's def, empty unarmed.</summary>
    public string Primary { get; init; } = "";

    /// <summary>A worn shield belt.</summary>
    public bool ShieldBelt { get; init; }

    /// <summary>The primary's def rows.</summary>
    public WeaponDef PrimaryFacts { get; init; } = new();

    public PawnId Pawn => Candidate.Pawn;
}

/// <summary>One stocked, unworn shield belt.</summary>
public sealed record LoadoutApparel(string Thing, string Definition, Cell Cell);

/// <summary>One equip (Wear false) or wear (Wear true) order, sent through the existing equip and gear actions.</summary>
public sealed record LoadoutOrder(PawnId Pawn, string Thing, string Definition, Cell Cell, bool Wear);

public static partial class PolicyFunctions
{
    /// <summary>Out-ranges a great bow (30).</summary>
    internal const int LoadoutTribalRange = 32;

    /// <summary>A close-range weapon: shotgun, SMG, pistol.</summary>
    internal const int LoadoutPodsRange = 26;

    /// <summary>Names the loadout a fight's view calls for. A prison break is not in the combat view; the caller says so.</summary>
    public static LoadoutThreat ClassifyLoadoutThreat(CombatView view, bool prisonBreak)
    {
        if (prisonBreak)
        {
            return LoadoutThreat.PrisonBreak;
        }

        if (view.Pods.IsKnown)
        {
            return LoadoutThreat.Pods;
        }

        var hostile = new Dictionary<PawnId, bool>();
        foreach (var t in view.Threats.Items)
        {
            hostile[ThreatPawn(t)] = !t.Building && !Positive(t.Dead) && !Positive(t.Downed);
        }

        var tribal = false;
        foreach (var p in view.Pawns.Items)
        {
            if (!hostile.GetValueOrDefault(p.Id) || p.Dead || p.Downed)
            {
                continue;
            }

            if (p.Mech)
            {
                return LoadoutThreat.Emp;
            }

            if (p.Shield.IsKnown && !p.WeaponFacts.Ranged)
            {
                return LoadoutThreat.Emp;
            }

            tribal = tribal || p.Kind.StartsWith("Tribal_", StringComparison.Ordinal);
        }

        return tribal ? LoadoutThreat.Tribal : LoadoutThreat.None;
    }

    /// <summary>
    /// The loadout decision: (threat, stocked gear, defenders) to equip orders before a fight's first order.
    /// Each thing and pawn is used once; ties break by pawn then thing id.
    /// pods: every non-melee defender takes the highest-DPS weapon of range at most LoadoutPodsRange that beats
    /// its primary. tribal: every non-melee defender whose primary is under LoadoutTribalRange takes the
    /// highest-DPS weapon of at least that range. emp: each squad without an EMP primary gets one carrier, its
    /// best shooter among non-melee defenders, holding an EMP weapon. prison break: wardens not holding a blunt
    /// weapon take one. Whatever the threat, a melee fighter without a shield belt wears a stocked one (a
    /// shield belt blocks its wearer's own shots).
    /// </summary>
    public static List<LoadoutOrder> ThreatLoadout(
        LoadoutThreat threat,
        IReadOnlyList<LoadoutDefender> defenderList,
        IReadOnlyList<EquipCandidateWeapon> weaponList,
        IReadOnlyList<LoadoutApparel> beltList)
    {
        var defenders = PolicySort.ByLess(defenderList, (a, b) => a.Pawn < b.Pawn);
        var weapons = PolicySort.ByLess(weaponList, (a, b) => string.CompareOrdinal(a.Thing, b.Thing) < 0);
        var used = new HashSet<string>();
        var output = new List<LoadoutOrder>();

        void Take(LoadoutDefender d, Func<EquipCandidateWeapon, bool> fits, Func<EquipCandidateWeapon, EquipCandidateWeapon, bool> better)
        {
            var best = -1;
            for (var i = 0; i < weapons.Count; i++)
            {
                var w = weapons[i];
                if (used.Contains(w.Thing) || !fits(w) || (ScoreWeapon(LoadoutPawn(d), w) <= 0 && !w.Facts.Emp))
                {
                    continue;
                }

                if (best < 0 || better(w, weapons[best]))
                {
                    best = i;
                }
            }

            if (best >= 0)
            {
                var w = weapons[best];
                used.Add(w.Thing);
                output.Add(new LoadoutOrder(d.Pawn, w.Thing, w.Definition, w.Cell, false));
            }
        }

        bool ByDps(EquipCandidateWeapon a, EquipCandidateWeapon b) => a.Facts.Dps > b.Facts.Dps;

        switch (threat)
        {
            case LoadoutThreat.Pods:
            case LoadoutThreat.Tribal:
                foreach (var d in defenders)
                {
                    if (!LoadoutReady(d) || LoadoutMelee(d))
                    {
                        continue;
                    }

                    var current = d.PrimaryFacts;
                    Func<EquipCandidateWeapon, bool> fits;
                    if (threat == LoadoutThreat.Pods)
                    {
                        fits = w => w.Class == WeaponClass.Ranged && !w.Facts.ForcedMiss && w.Facts.Range <= LoadoutPodsRange && w.Facts.Dps > current.Dps;
                    }
                    else
                    {
                        if (d.Primary.Length != 0 && current.Range >= LoadoutTribalRange)
                        {
                            continue;
                        }

                        fits = w => w.Class == WeaponClass.Ranged && !w.Facts.ForcedMiss && w.Facts.Range >= LoadoutTribalRange;
                    }

                    Take(d, fits, ByDps);
                }

                break;
            case LoadoutThreat.Emp:
                var carried = new HashSet<string>();
                foreach (var d in defenders)
                {
                    if (d.PrimaryFacts.Emp)
                    {
                        carried.Add(d.Squad);
                    }
                }

                var carrier = new Dictionary<string, LoadoutDefender>();
                foreach (var d in defenders)
                {
                    if (carried.Contains(d.Squad) || !LoadoutReady(d) || LoadoutMelee(d))
                    {
                        continue;
                    }

                    if (!carrier.TryGetValue(d.Squad, out var c) || Shooting(d) > Shooting(c))
                    {
                        carrier[d.Squad] = d;
                    }
                }

                var squads = carrier.Keys.ToList();
                squads.Sort(StringComparer.Ordinal);
                foreach (var s in squads)
                {
                    // The grenade first (EMP is thrown as the carrier's equipped primary): the shorter-ranged EMP weapon is the grenade.
                    Take(carrier[s], w => w.Facts.Emp, (a, b) => a.Facts.Range < b.Facts.Range);
                }

                break;
            case LoadoutThreat.PrisonBreak:
                foreach (var d in defenders)
                {
                    if (d.Warden && LoadoutReady(d) && !d.PrimaryFacts.Blunt)
                    {
                        Take(d, w => w.Facts.Blunt, ByDps);
                    }
                }

                break;
        }

        var belts = PolicySort.ByLess(beltList, (a, b) => string.CompareOrdinal(a.Thing, b.Thing) < 0);
        var next = 0;
        foreach (var d in defenders)
        {
            if (next >= belts.Count || d.ShieldBelt || !LoadoutReady(d) || !LoadoutMelee(d))
            {
                continue;
            }

            var b = belts[next];
            next++;
            output.Add(new LoadoutOrder(d.Pawn, b.Thing, b.Definition, b.Cell, true));
        }

        return output;
    }

    /// <summary>Scores a loadout pick as a lone fighter so the EMP and area weapons the threat rules name are not zeroed out.</summary>
    private static EquipCandidatePawn LoadoutPawn(LoadoutDefender d) => d.Candidate with { LoneFighter = true };

    /// <summary>A live, capable defender not in a mental state. Drafted is fine: the loadout runs as the fight starts.</summary>
    private static bool LoadoutReady(LoadoutDefender d)
    {
        var p = d.Candidate;
        foreach (var f in new[] { p.Dead, p.Downed, p.MentalState, p.IncapableOfViolence })
        {
            if (!f.TryGetValue(out var v) || v)
            {
                return false;
            }
        }

        return p.Role != WeaponRole.NonCombatant && p.NoArms.Length == 0;
    }

    /// <summary>A melee fighter: the melee role, a melee-only pawn, or one holding a melee weapon.</summary>
    private static bool LoadoutMelee(LoadoutDefender d)
    {
        var p = d.Candidate;
        if (p.Role == WeaponRole.Melee || p.Profile.Effects.MeleeOnly || p.ShootingDisabled)
        {
            return true;
        }

        return d.PrimaryFacts.Melee;
    }

    private static int Shooting(LoadoutDefender d) =>
        d.Candidate.Profile.Skills.TryGetValue("Shooting", out var skill) ? skill.Level : 0;
}
