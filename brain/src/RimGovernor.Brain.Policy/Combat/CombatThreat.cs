namespace RimGovernor.Brain.Policy;

/// <summary>
/// Where the strip of a downed raider stands: not yet issued, its action still open, the designation placed,
/// or the action refused or cancelled.
/// </summary>
public enum StripState
{
    StripNone = 0,
    StripOpen,
    StripPlaced,
    StripRefused,
}

/// <summary>
/// The fight's next step for one downed raider: strip it, wait for the strip, or it is done (stripped). A
/// raider not <see cref="PolicyFunctions.CaptureWorthy"/> is then finished; a worthy one is left to capture.
/// </summary>
public enum PostFightStep
{
    Strip,
    Wait,
    Done,
}

public static partial class PolicyFunctions
{
    // Threat tiers, most urgent first: the caster of a psychic ritual, rocketeers and grenadiers, then
    // anything in melee with a colonist, then sappers (a breaching termite among them), then the other mechs
    // (inferno-cannon centipede > scyther > termite > the rest; a termite strips our cover), then everyone
    // else. A go-juice raider does not go down from pain, so it ranks just after the sappers, ahead of the
    // tribal tiers. Among tribals a berserker, fast and melee, ranks ahead of a pila thrower; both come after
    // sappers and ahead of the mechs they never raid with.
    internal const int ThreatRitualCaster = 0;
    internal const int ThreatExplosive = 1;
    internal const int ThreatMeleeColonist = 2;
    internal const int ThreatSapper = 3;
    internal const int ThreatGoJuice = 4;
    internal const int ThreatBerserker = 5;
    internal const int ThreatPila = 6;
    internal const int ThreatInfernoCentipede = 7;
    internal const int ThreatScyther = 8;
    internal const int ThreatTermite = 9;
    internal const int ThreatMech = 10;
    internal const int ThreatOther = 11;

    /// <summary>
    /// Judgment table: the game states no field that sets a tribal berserker or a particular mech apart (a
    /// scyther and a termite carry no weapon def), so the tiers name the defs. The flamer needs no weapon
    /// entry: Gun_InfernoCannon's verb is explosive, so it already ranks <see cref="ThreatExplosive"/> above.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, int> ThreatKinds = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Tribal_Berserker"] = ThreatBerserker,
        ["Mech_CentipedeBurner"] = ThreatInfernoCentipede,
        ["Mech_Scyther"] = ThreatScyther,
        ["Mech_Termite_Breach"] = ThreatTermite,
    };

    /// <summary>The weapon half of the judgment table: the pila's verb differs from a bow's only in numbers.</summary>
    internal static readonly IReadOnlyDictionary<string, int> ThreatWeapons = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Pila"] = ThreatPila,
    };

    /// <summary>
    /// Ranks one live hostile; colonists are our defenders. A raider with an explosive weapon (a launcher or a
    /// grenade, by its def rows) is a rocketeer or grenadier.
    /// </summary>
    internal static int ThreatTier(CombatPawnState h, IReadOnlySet<PawnId> colonists)
    {
        if (h.WeaponFacts.Explosive)
        {
            return ThreatExplosive;
        }
        if (h.Stance == CombatStance.Melee && colonists.Contains(h.Target))
        {
            return ThreatMeleeColonist;
        }
        if (h.Sapper)
        {
            return ThreatSapper;
        }
        if (h.GoJuice)
        {
            return ThreatGoJuice;
        }
        // The named tiers below rank the more urgent of the kind's and the weapon's (the constants run most
        // urgent first).
        var named = ThreatOther;
        if (ThreatKinds.TryGetValue(h.Kind, out var tier))
        {
            named = tier;
        }
        if (ThreatWeapons.TryGetValue(h.Weapon, out tier))
        {
            named = Math.Min(named, tier);
        }
        if (named != ThreatOther)
        {
            return named;
        }
        if (h.Mech)
        {
            return ThreatMech;
        }
        return ThreatOther;
    }

    /// <summary>
    /// Says a downed hostile is worth taking prisoner: a luciferium addict dies without a supply we would have
    /// to keep up, so the after-combat step strips or finishes it instead.
    /// </summary>
    public static bool CaptureWorthy(CombatPawnState h) => !h.Luciferium;

    /// <summary>
    /// Strips a downed raider still wearing apparel (or of unknown apparel) and waits until its apparel is
    /// gone. A refused strip, or a placed one whose apparel the frame no longer shows, is done rather than
    /// holding the fight on a missing row.
    /// </summary>
    public static PostFightStep PostFightNext(Fact<bool> worn, StripState strip)
    {
        var known = worn.TryGetValue(out var w);
        if (strip == StripState.StripOpen)
        {
            return PostFightStep.Wait;
        }
        if (known && !w)
        {
            return PostFightStep.Done;
        }
        if (strip == StripState.StripNone)
        {
            return PostFightStep.Strip;
        }
        if (strip == StripState.StripPlaced && known && w)
        {
            return PostFightStep.Wait;
        }
        return PostFightStep.Done;
    }

    /// <summary>
    /// The live hostile pawns (not buildings) by threat score, ties by id: the order gunners focus fire in. A
    /// pawn hidden from the player is not one a gunner can be ordered at.
    /// </summary>
    internal static List<CombatPawnState> RankThreats(CombatView view)
    {
        // Index the pawn rows rather than copying them into a map: the later row of a repeated id wins.
        var state = new Dictionary<PawnId, int>(view.Pawns.Count);
        for (var i = 0; i < view.Pawns.Count; i++)
        {
            state[view.Pawns[i].Id] = i;
        }
        var colonists = new HashSet<PawnId>(view.Defenders.Select(d => d.Id));
        var result = new List<CombatPawnState>();
        var tier = new Dictionary<PawnId, int>(view.Threats.Count);
        foreach (var t in view.Threats)
        {
            var id = new PawnId(t.Id);
            var s = state.TryGetValue(id, out var index) ? view.Pawns[index] : new CombatPawnState { Id = id };
            if (t.Building || Positive(t.Dead) || Positive(t.Downed) || s.Dead || s.Downed || HiddenFromPlayer(t))
            {
                continue;
            }
            tier[id] = ThreatTier(s, colonists);
            if (RitualCaster(t))
            {
                tier[id] = ThreatRitualCaster;
            }
            result.Add(s);
        }
        return [.. result.OrderBy(p => tier[p.Id]).ThenBy(p => p.Id)];
    }

    /// <summary>Says a shooter at <paramref name="from"/> with weapon range <paramref name="reach"/> can reach <paramref name="h"/>; an unknown cell or range counts as in range.</summary>
    internal static bool InRange(Fact<Cell> from, double reach, CombatPawnState h)
    {
        if (!from.TryGetValue(out var a) || !h.Cell.TryGetValue(out var b) || reach <= 0)
        {
            return true;
        }
        double dx = a.X - b.X;
        double dz = a.Z - b.Z;
        return Math.Sqrt((dx * dx) + (dz * dz)) <= reach;
    }

    /// <summary>
    /// Points every gunner (a ranged role) at the top-scored hostile in its range, keeping the target it had
    /// before this stop (<paramref name="prior"/>) while that one lives and stays in range, so the focus is
    /// stable across stops. A gunner with no hostile in range keeps the formation's target.
    /// </summary>
    internal static List<CombatRole> FocusFire(CombatView view, IReadOnlyList<CombatRole> roles, IReadOnlyList<CombatRole> prior)
    {
        // An exploder near our pawns is never shot.
        var near = NearExploders(view);
        var ranked = RankThreats(view).Where(h => !near.GetValueOrDefault(h.Id)).ToList();
        var live = new Dictionary<PawnId, CombatPawnState>();
        foreach (var h in ranked)
        {
            live[h.Id] = h;
        }
        var was = new Dictionary<PawnId, PawnId>();
        foreach (var r in prior)
        {
            was[r.Pawn] = r.Target;
        }
        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns)
        {
            state[p.Id] = p;
        }
        var locked = MeleeLocked(view, roles);
        var result = roles.ToList();
        for (var i = 0; i < result.Count; i++)
        {
            var r = result[i];
            if (!r.Ranged)
            {
                continue;
            }
            state.TryGetValue(r.Pawn, out var s);
            s ??= new CombatPawnState();
            var from = s.Cell;
            if (r.Cell is { } roleCell)
            {
                from = Known(roleCell);
            }
            // A hostile locked with our blocker is passed over while another is in range.
            Func<CombatPawnState, bool> pick = h => InRange(from, s.WeaponRange, h) && !locked.GetValueOrDefault(h.Id);
            if (!ranked.Any(pick))
            {
                pick = h => InRange(from, s.WeaponRange, h);
            }
            if (live.TryGetValue(was.GetValueOrDefault(r.Pawn), out var kept) && pick(kept))
            {
                result[i] = r with { Target = kept.Id };
                continue;
            }
            var j = ranked.FindIndex(h => pick(h));
            if (j >= 0)
            {
                r = r with { Target = ranked[j].Id };
            }
            if (near.GetValueOrDefault(r.Target))
            {
                r = r with { Target = default };
            }
            result[i] = r;
        }
        return result;
    }
}
