namespace RimGovernor.Brain.Policy;

/// <summary>What the siege tactic does at a stop.</summary>
public enum SiegeMode
{
    None = 0,

    /// <summary>Keeps everyone home: before the supplies land (attacking then makes them flee), or once the sandbags are up.</summary>
    Hold,

    /// <summary>Attacks the camp while it builds.</summary>
    Sortie,

    /// <summary>Sends the gunners that outrange the camp to shoot it once the sandbags are up, until the lord assaults.</summary>
    Harass,

    /// <summary>Attacks a sleeping camp with everyone.</summary>
    Sneak,
}

public static partial class PolicyFunctions
{
    // Siege lord classes.
    internal const string SiegeLordJob = "LordJob_Siege";
    internal const string SiegeCampToil = "LordToil_Siege";
    internal const string SiegeTravel = "LordToil_Travel";

    /// <summary>The live hostiles of a siege lord still travelling or camped (not yet assaulting), with their toil.</summary>
    internal static Dictionary<PawnId, string> LiveBesiegers(CombatView view)
    {
        var down = DownPawns(view);
        var result = new Dictionary<PawnId, string>();
        foreach (var t in view.Positional)
        {
            t.LordJobClass.TryGetValue(out var job);
            t.LordToilClass.TryGetValue(out var toil);
            var id = new PawnId(t.Id);
            if (job != SiegeLordJob || Positive(t.Dead) || Positive(t.Downed) || down.GetValueOrDefault(id))
            {
                continue;
            }
            if (toil == SiegeCampToil || toil == SiegeTravel)
            {
                result[id] = toil;
            }
        }
        return result;
    }

    /// <summary>
    /// Records the tick the siege camp is first seen and latches the first hostile mortar frame: the builders
    /// start on the mortars once the sandbags are up, so the sortie ends there.
    /// </summary>
    internal static CombatMemory SiegeTurn(CombatView view, CombatMemory m)
    {
        var besiegers = LiveBesiegers(view);
        if (m.SiegeCamp.Value == 0 && besiegers.Values.Any(toil => toil == SiegeCampToil))
        {
            m = m with { SiegeCamp = view.Tick };
        }
        if (m.SiegeCamp.Value == 0 || m.SiegeMortar)
        {
            return m;
        }
        foreach (var p in view.Pawns)
        {
            if (besiegers.ContainsKey(p.Id) && p.Job == "FinishFrame" && p.TargetMortar)
            {
                return m with { SiegeMortar = true };
            }
        }
        foreach (var s in view.Structures)
        {
            if (s.Mortar)
            {
                return m with { SiegeMortar = true };
            }
        }
        return m;
    }

    /// <summary>
    /// The siege tactic's mode at this stop, None when no siege lord is travelling or camped: hold before the
    /// camp is set, sortie until the first hostile mortar frame, then harass.
    /// </summary>
    internal static SiegeMode SiegeModeOf(CombatView view, CombatMemory m)
    {
        if (LiveBesiegers(view).Count == 0)
        {
            return SiegeMode.None;
        }
        if (m.SiegeCamp.Value == 0)
        {
            return SiegeMode.Hold;
        }
        if (CampAsleep(view))
        {
            return SiegeMode.Sneak;
        }
        if (m.SiegeMortar)
        {
            return SiegeMode.Harass;
        }
        return SiegeMode.Sortie;
    }

    /// <summary>
    /// Assigns the siege roles for <paramref name="mode"/>. Hold: gunners to the layout's firing cells (when
    /// there is a layout) with no target, brawlers nothing, so nobody walks out. Sortie: every armed defender
    /// attacks the top-ranked besieger.
    /// </summary>
    internal static List<CombatRole> SiegeFormation(CombatView view, SiegeMode mode)
    {
        PawnId top = default;
        var besiegers = LiveBesiegers(view);
        foreach (var h in RankThreats(view))
        {
            if (besiegers.ContainsKey(h.Id))
            {
                top = h.Id;
                break;
            }
        }
        IReadOnlyList<Cell> firing = [];
        if (view.Layout.TryGetValue(out var layout))
        {
            firing = layout.Firing;
        }
        var roles = new List<CombatRole>();
        var i = 0;
        foreach (var d in view.Defenders)
        {
            if (!SquadDefenderEligible(d) || !Positive(d.Armed))
            {
                continue;
            }
            var ranged = Positive(d.RangedEquipped);
            var role = new CombatRole { Pawn = d.Id, Ranged = ranged };
            if (mode == SiegeMode.Sortie || mode == SiegeMode.Sneak)
            {
                role = role with { Target = top };
            }
            else if (!ranged)
            {
                continue;
            }
            else if (i < firing.Count)
            {
                role = role with { Cell = firing[i] };
                i++;
            }
            roles.Add(role);
        }
        if (mode == SiegeMode.Harass)
        {
            roles = HarassRoles(view, roles);
        }
        return SortRoles(roles);
    }

    /// <summary>The siege row of the reaction table: a siege starting, a mode change, the lord assaulting (no besieger left), or a sortie target down re-forms.</summary>
    internal static bool ReformSiege(CombatView view, CombatMemory m)
    {
        var mode = SiegeModeOf(view, m);
        return m.Tactic != CombatTactic.Siege || mode != m.SiegeMode || SquadTargetDown(view, m);
    }

    /// <summary>
    /// Clears the targets focus fire gave a siege's home gunners (every role but a harasser, outside the
    /// sortie): an attack order on a hostile at the camp walks the gunner out to it.
    /// </summary>
    internal static CombatMemory SiegeHold(CombatMemory m)
    {
        if (m.Tactic != CombatTactic.Siege || m.SiegeMode == SiegeMode.Sortie || m.SiegeMode == SiegeMode.Sneak)
        {
            return m;
        }
        return m with { Roles = EquatableArray.From(m.Roles.Select(r => r.Duty != CombatDuty.Harasser ? r with { Target = default } : r)) };
    }
}
