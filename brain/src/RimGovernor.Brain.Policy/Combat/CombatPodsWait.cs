namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>The lord toils of a raid that flees or loots and leaves: the pods tactic's wait ends and it strikes.</summary>
    internal static readonly IReadOnlySet<string> PodStrikeToils = new HashSet<string>(StringComparer.Ordinal)
    {
        "LordToil_PanicFlee",
        "LordToil_KidnapCover",
        "LordToil_StealCover",
        "LordToil_ExitMap",
        "LordToil_ExitMapAndEscortCarriers",
    };

    /// <summary>
    /// The strength comparison of the wait option: ours is the health fraction summed over the armed, eligible
    /// defenders; the pod group's is its live hostile count, or the landing cell count while none is out yet.
    /// </summary>
    internal static (double Ours, double Theirs) PodStrength(CombatView view, PodArrival pods)
    {
        double ours = 0;
        double theirs = 0;
        foreach (var d in view.Defenders)
        {
            if (SquadDefenderEligible(d) && Positive(d.Armed))
            {
                d.HealthFraction.TryGetValue(out var h);
                ours += h;
            }
        }
        var down = new HashSet<PawnId>();
        foreach (var p in view.Pawns)
        {
            if (p.Dead || p.Downed)
            {
                down.Add(p.Id);
            }
        }
        foreach (var t in view.Positional)
        {
            if (!Positive(t.Dead) && !Positive(t.Downed) && !down.Contains(t.Id))
            {
                theirs++;
            }
        }
        if (theirs == 0)
        {
            theirs = pods.Landing.Count;
        }
        return (ours, theirs);
    }

    /// <summary>Reports a raid_phase stop with a live hostile fleeing or looting and leaving.</summary>
    internal static bool PodStrike(CombatView view, StopEvent stop)
    {
        if (stop.Kind != CombatStopKind.RaidPhase)
        {
            return false;
        }
        foreach (var t in view.Positional)
        {
            if (t.LordToilClass.TryGetValue(out var toil) && PodStrikeToils.Contains(toil) && !Positive(t.Dead) && !Positive(t.Downed))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Decides the wait option at a pods formation: once the raid flees or loots and leaves the fight strikes
    /// for good; until then it waits while our strength is below the pod group's. Returns the updated memory.
    /// </summary>
    internal static (CombatMemory Memory, bool Wait) PodWait(CombatView view, StopEvent stop, CombatMemory m)
    {
        if (m.PodWait && PodStrike(view, stop))
        {
            m = m with { PodStruck = true };
        }
        var (ours, theirs) = PodStrength(view, m.Pods!);
        m = m with { PodWait = !m.PodStruck && ours < theirs };
        return (m, m.PodWait);
    }

    /// <summary>
    /// Turns the pod response into the wait: nobody engages, the doorway pairs hold their cells behind the
    /// landing room's doors, and the doors are closed.
    /// </summary>
    internal static (IReadOnlyList<CombatRole> Roles, IReadOnlyList<PodDoor> Doors) HoldBehindDoors(IReadOnlyList<CombatRole> roles, IReadOnlyList<PodDoor> doors) =>
        (roles.Select(r => r with { Target = default }).ToList(), doors.Select(d => d with { Mode = DoorMode.Close }).ToList());
}
