namespace RimGovernor.Brain.Policy;

/// <summary>
/// One MaintainShelter sheltering move: a pawn's allowed area set to the Safe area, or cleared (empty) once
/// no trigger holds.
/// </summary>
public sealed record AllowedAreaChange
{
    public PawnId Pawn { get; init; }

    public bool Animal { get; init; }

    public string Area { get; init; } = "";
}

/// <summary>Why pawns shelter now.</summary>
public enum ShelterTrigger
{
    [WireName("")]
    None = 0,

    [WireName("threat")]
    Threat,

    [WireName("fallout")]
    Fallout,

    [WireName("temperature")]
    Temperature,
}

public static partial class PolicyFunctions
{
    /// <summary>The native label of the bot-owned Safe allowed area; AreaIntent labels an area with its plain key.</summary>
    public const string SafeAreaLabel = SafeAreaKey;

    /// <summary>The bot area key of the NoDanger allowed area: the home area minus the danger cells.</summary>
    public const string NoDangerAreaKey = "NoDanger";

    /// <summary>The native label of the NoDanger area.</summary>
    public const string NoDangerAreaLabel = NoDangerAreaKey;

    /// <summary>How long after the last live, unrestrained hostile haulers stay out of the danger cells (one in-game hour).</summary>
    public static readonly Tick DangerCooldown = new(GameTime.TicksPerHour);

    /// <summary>
    /// Whether haulers are kept out of the danger cells: a hostile is live (hostiles &gt; 0) or the last one
    /// was seen (lastThreat, when lastKnown) less than DangerCooldown before now. Unknown with hostiles.
    /// </summary>
    public static Fact<bool> DangerWindowOf(Fact<long> hostiles, Tick lastThreat, bool lastKnown, Tick now)
    {
        if (!hostiles.TryGetValue(out var n))
        {
            return Unknown<bool>();
        }
        return Known(n > 0 || (lastKnown && now.Value - lastThreat.Value < DangerCooldown.Value));
    }

    /// <summary>The work pawns with Hauling enabled (priority 1..4). Unknown when any row's work is unknown.</summary>
    public static Fact<EquatableArray<PawnId>> DangerHaulers(Fact<EquatableArray<WorkPawn>> workers)
    {
        if (!workers.TryGetValue(out var rows))
        {
            return Unknown<EquatableArray<PawnId>>();
        }
        var output = new List<PawnId>();
        foreach (var w in rows)
        {
            if (!w.Work.TryGetValue(out var work))
            {
                return Unknown<EquatableArray<PawnId>>();
            }
            foreach (var p in work)
            {
                if (p.Work == WorkType.Hauling && !p.Disabled && p.Priority > 0)
                {
                    output.Add(w.Id);
                }
            }
        }
        return Known(EquatableArray.From(output));
    }

    /// <summary>
    /// The cells a hauler must keep away from, from the native threat census: every live, discovered hostile
    /// pawn's cell and every hostile building's occupied cells, passive hives and dormant clusters included (a
    /// hive is danger though it holds no fight). Sorted, deduplicated.
    /// </summary>
    public static List<Cell> DangerSeeds(IReadOnlyList<EmergencyThreat> threats)
    {
        var set = new HashSet<Cell>();
        foreach (var t in threats)
        {
            if ((t.Kind != ThreatKind.Hostile && t.Kind != ThreatKind.HuntingPredator && t.Kind != ThreatKind.HostileBuilding) || t.Undiscovered)
            {
                continue;
            }
            if (t.Dead.TryGetValue(out var dead) && dead)
            {
                continue;
            }
            if (t.Downed.TryGetValue(out var downed) && downed)
            {
                continue;
            }
            IReadOnlyList<Cell> cells = t.Cells;
            if (t.Position.TryGetValue(out var at) && !t.Building)
            {
                cells = [at];
            }
            foreach (var c in cells)
            {
                set.Add(c);
            }
        }
        return SortedCells(set);
    }

    /// <summary>Whether cell lies within ThreatReachCells of a seed.</summary>
    public static bool NearDanger(IReadOnlyList<Cell> seeds, Cell cell)
    {
        foreach (var s in seeds)
        {
            if (Math.Abs(cell.X - s.X) <= ThreatReachCells && Math.Abs(cell.Z - s.Z) <= ThreatReachCells)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The home area minus the killbox cells and every cell near a danger seed, sorted.</summary>
    public static List<Cell> NoDangerCells(IReadOnlyList<Cell> home, IReadOnlyList<Cell> killbox, IReadOnlyList<Cell> seeds)
    {
        var excluded = new HashSet<Cell>(killbox);
        var set = new HashSet<Cell>();
        foreach (var c in home)
        {
            if (!excluded.Contains(c) && !NearDanger(seeds, c))
            {
                set.Add(c);
            }
        }
        return SortedCells(set);
    }

    /// <summary>Emergency findings guide response planning and reporting.</summary>
    public static int ShelterPriority(ShelterTrigger trigger) => trigger == ShelterTrigger.Threat ? 1 : 2;

    /// <summary>
    /// The active sheltering trigger: toxic fallout, a cold snap or heat wave whose outdoor temperature is
    /// outside the sleeping comfort range, or hostiles on the map (a raid or manhunter pack). Known is false
    /// only when some trigger is not known clear.
    /// </summary>
    public static (ShelterTrigger Trigger, bool Known) ShelterTriggerOf(RoundsFacts f)
    {
        var ck = f.DisasterConditions.TryGetValue(out var conditions);
        var weather = false;
        if (ck)
        {
            foreach (var c in conditions)
            {
                if (c.Definition == ConditionToxicFallout)
                {
                    return (ShelterTrigger.Fallout, true);
                }
                if (c.Definition == ConditionColdSnap || c.Definition == ConditionHeatWave)
                {
                    weather = true;
                }
            }
        }
        var tempKnown = true;
        if (weather)
        {
            var ok = f.OutdoorTemperature.TryGetValue(out var outside);
            var lk = f.SleepingMin.TryGetValue(out var lo);
            var hk0 = f.SleepingMax.TryGetValue(out var hi);
            tempKnown = ok && lk && hk0;
            if (tempKnown && (outside < lo || outside > hi))
            {
                return (ShelterTrigger.Temperature, true);
            }
        }
        var hk = f.Hostiles.TryGetValue(out var hostiles);
        if (hk && hostiles > 0)
        {
            return (ShelterTrigger.Threat, true);
        }
        return (ShelterTrigger.None, ck && tempKnown && hk);
    }

    /// <summary>
    /// MaintainShelter's sheltering planner. While a trigger holds, undrafted colonists (for a threat, those
    /// outside the squad's draft set, ShelterCombatants) and animals that take areas without a pen are moved
    /// into the Safe area. Once every trigger is known clear, pawns still restricted to the Safe area go back
    /// to unrestricted. Haulers not sheltered are kept to the NoDanger area while DangerWindow holds, and go
    /// back to unrestricted once it is known closed; the colonist named by an open dash window (DashOpenFor)
    /// is let out of Safe while the trigger is cold alone, and sheltered again at the window's bound; other
    /// areas are left to their own planners. It keeps no history: the same facts give the same moves after a
    /// restart or reload.
    /// </summary>
    public static List<AllowedAreaChange> PlanSheltering(RoundsFacts f)
    {
        f.ShelterArea.TryGetValue(out var safe);
        safe ??= "";
        var (trigger, tk) = ShelterTriggerOf(f);
        if (!tk)
        {
            return [];
        }
        f.NoDangerArea.TryGetValue(out var noKill);
        noKill ??= "";
        var windowKnown = f.DangerWindow.TryGetValue(out var window);
        var hk = f.DangerHaulers.TryGetValue(out var haulerRows);
        var haulers = new HashSet<PawnId>();
        if (hk)
        {
            foreach (var id in haulerRows)
            {
                haulers.Add(id);
            }
        }
        HashSet<PawnId>? combatants = null;
        if (trigger == ShelterTrigger.Threat && f.ShelterCombatants.TryGetValue(out var combatantRows))
        {
            combatants = [.. combatantRows];
        }
        var hostilesKnown = f.Hostiles.TryGetValue(out var hostiles);
        var calm = hostilesKnown && hostiles == 0;
        var changes = new List<AllowedAreaChange>();

        void Add(PawnId id, bool animal, bool shelter, Fact<string> current)
        {
            var dash = !animal && trigger == ShelterTrigger.Temperature && calm && f.DashOpenFor(id);
            shelter = shelter && !dash;
            if (!current.TryGetValue(out var area))
            {
                return;
            }
            var hauler = !animal && haulers.Contains(id);
            var to = "";
            var move = false;
            if (shelter && safe != "")
            {
                to = safe;
                move = area != safe;
            }
            else if (noKill != "" && windowKnown && window && hk && hauler)
            {
                // Haulers stay out of the danger cells through the fight and its cooldown.
                to = noKill;
                move = area != noKill;
            }
            else if (safe != "" && area == safe)
            {
                // An open dash window lets the named pawn out of Safe for the cold only.
                move = trigger == ShelterTrigger.None || dash;
            }
            else if (noKill != "" && area == noKill)
            {
                move = windowKnown && (!window || (hk && !hauler));
            }
            // Native no longer vetoes an unroofed area during fallout, so the only area a fallout move may
            // land on is the roofed Safe area.
            if (trigger == ShelterTrigger.Fallout && (safe == "" || to != safe))
            {
                move = false;
            }
            if (move)
            {
                changes.Add(new AllowedAreaChange { Pawn = id, Animal = animal, Area = to });
            }
        }

        var known = f.RecoverySafety.TryGetValue(out var safety);
        var wk = f.RecoveryWorkers.TryGetValue(out var workers);
        if (known && wk)
        {
            foreach (var worker in workers)
            {
                if (!AreaKnownFalse(worker.Dead) || !AreaKnownFalse(worker.Downed) || !AreaKnownFalse(worker.Drafted) || !AreaKnownFalse(worker.Mental))
                {
                    continue;
                }
                // A threat shelters only pawns known outside the draft set.
                var shelter = trigger != ShelterTrigger.None && (trigger != ShelterTrigger.Threat || (combatants is not null && !combatants.Contains(worker.Pawn)));
                foreach (var restriction in safety!.Restrictions)
                {
                    if (restriction.Pawn == worker.Pawn)
                    {
                        Add(worker.Pawn, false, shelter, restriction.Area);
                        break;
                    }
                }
            }
        }
        if (f.AnimalUpkeep.Animals.TryGetValue(out var animals))
        {
            foreach (var animal in animals)
            {
                if (!Positive(animal.SupportsAreas) || !AreaKnownFalse(animal.RequiresPen))
                {
                    continue;
                }
                Add(animal.Id, true, trigger != ShelterTrigger.None, animal.AllowedArea);
            }
        }
        // Stable by pawn id.
        return changes.OrderBy(c => c.Pawn).ToList();
    }

    internal static bool AreaKnownFalse(Fact<bool> f) => f.TryGetValue(out var v) && !v;

    /// <summary>
    /// Whether a threat's sheltering response is complete: a threat triggers sheltering and every living,
    /// undowned, undrafted, sane colonist (at least one) is restricted to the Safe area. The clock window then
    /// watches the threat instead of refusing it: sheltered colonists wait it out on game time.
    /// </summary>
    public static bool ShelterHeld(RoundsFacts f)
    {
        var (trigger, tk) = ShelterTriggerOf(f);
        var sk = f.ShelterArea.TryGetValue(out var safe);
        var known = f.RecoverySafety.TryGetValue(out var safety);
        var wk = f.RecoveryWorkers.TryGetValue(out var workers);
        if (!tk || trigger != ShelterTrigger.Threat || !sk || string.IsNullOrEmpty(safe) || !known || !wk)
        {
            return false;
        }
        var held = 0;
        foreach (var worker in workers)
        {
            if (!AreaKnownFalse(worker.Dead) || !AreaKnownFalse(worker.Downed) || !AreaKnownFalse(worker.Mental))
            {
                continue;
            }
            if (!worker.Drafted.TryGetValue(out var drafted))
            {
                return false;
            }
            if (drafted)
            {
                continue;
            }
            var inside = false;
            foreach (var restriction in safety!.Restrictions)
            {
                if (restriction.Pawn == worker.Pawn)
                {
                    inside = restriction.Area.TryGetValue(out var area) && area == safe;
                    break;
                }
            }
            if (!inside)
            {
                return false;
            }
            held++;
        }
        return held > 0;
    }
}
