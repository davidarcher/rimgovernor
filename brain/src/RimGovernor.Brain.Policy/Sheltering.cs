namespace RimGovernor.Brain.Policy;

/// <summary>
/// One shelter/safe-area sheltering move: a pawn's allowed area set to the Safe area, or cleared (empty) once
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

    AwaitingEmergency,

    NoIndoorBed,

    [WireName("threat")]
    Threat,

    [WireName("fallout")]
    Fallout,

    [WireName("temperature")]
    Temperature,
}

public static partial class ShelterRules
{
    /// <summary>The native label of the bot-owned Safe allowed area; AreaIntent labels an area with its plain key.</summary>
    public const string SafeAreaLabel = SafeAreaRules.SafeAreaKey;

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
    public static bool DangerWindowOf(HostileCensus hostiles, Tick lastThreat, bool lastKnown, Tick now) =>
        hostiles.ThreatCount > 0 || (lastKnown && now.Value - lastThreat.Value < DangerCooldown.Value);

    /// <summary>The work pawns with Hauling enabled (priority 1..4). Unknown when any row's work is unknown.</summary>
    public static EquatableArray<PawnId> DangerHaulers(WorkPawnsRead workers)
    {
        var rows = workers.Pawns;
        var output = new List<PawnId>();
        foreach (var w in rows)
        {
            foreach (var p in w.Work.Priorities())
            {
                if (p.Work == WorkType.Hauling && !p.Disabled && p.Priority > 0)
                {
                    output.Add(w.Id);
                }
            }
        }
        return EquatableArray.From(output);
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
            if (t.Dead || t.Downed)
            {
                continue;
            }
            IReadOnlyList<Cell> cells = t.Building ? t.Cells : [t.Position];
            foreach (var c in cells)
            {
                set.Add(c);
            }
        }
        return PlannedRules.SortedCells(set);
    }

    /// <summary>Whether cell lies within ThreatReachCells of a seed.</summary>
    public static bool NearDanger(IReadOnlyList<Cell> seeds, Cell cell)
    {
        foreach (var s in seeds)
        {
            if (Math.Abs(cell.X - s.X) <= SafetyOverlayRules.ThreatReachCells && Math.Abs(cell.Z - s.Z) <= SafetyOverlayRules.ThreatReachCells)
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
        return PlannedRules.SortedCells(set);
    }

    /// <summary>
    /// The active sheltering trigger: toxic fallout, a cold snap or heat wave whose outdoor temperature is
    /// outside the sleeping comfort range, or hostiles on the map (a raid or manhunter pack). Names absent
    /// indoor beds during weather and the emergency census before it is observed.
    /// </summary>
    public static ShelterTrigger ShelterTriggerOf(ReviewFacts f)
    {
        var conditions = f.DisasterConditions;
        var weather = false;
        foreach (var c in conditions)
        {
            if (c.Definition == DisasterRules.ConditionToxicFallout)
            {
                return ShelterTrigger.Fallout;
            }
            if (c.Definition == DisasterRules.ConditionColdSnap || c.Definition == DisasterRules.ConditionHeatWave)
            {
                weather = true;
            }
        }
        if (weather && f.Census.SleepingTemperature is RimGovernor.Brain.GameFacts.Observations.SleepingTemperature.Indoor indoor &&
            (f.Census.OutdoorTemperatureC < indoor.MinC || f.Census.OutdoorTemperatureC > indoor.MaxC))
        {
            return ShelterTrigger.Temperature;
        }
        if (f.Hostiles is HostileCensus.Active active && active.ThreatCount > 0)
        {
            return ShelterTrigger.Threat;
        }
        if (f.Hostiles is HostileCensus.AwaitingEmergency) return ShelterTrigger.AwaitingEmergency;
        if (f.Hostiles is HostileCensus.Quiet && weather && f.Census.SleepingTemperature is RimGovernor.Brain.GameFacts.Observations.SleepingTemperature.NoIndoorBed) return ShelterTrigger.NoIndoorBed;
        if (f.Hostiles is HostileCensus.Quiet or HostileCensus.Active) return ShelterTrigger.None;
        throw new DomainException("unrecognized hostile census");
    }

    /// <summary>
    /// The shelter/safe-area sheltering planner. While a trigger holds, undrafted colonists (for a threat, those
    /// outside the squad's draft set, ShelterCombatants) and animals that take areas without a pen are moved
    /// into the Safe area. Once every trigger is known clear, pawns still restricted to the Safe area go back
    /// to unrestricted. Haulers not sheltered are kept to the NoDanger area while DangerWindow holds, and go
    /// back to unrestricted once it is known closed; the colonist named by an open dash window (DashOpenFor)
    /// is let out of Safe while the trigger is cold alone, and sheltered again at the window's bound; other
    /// areas are left to their own planners. It keeps no history: the same facts give the same moves after a
    /// restart or reload.
    /// </summary>
    public static List<AllowedAreaChange> PlanSheltering(ReviewFacts f)
    {
        var safe = f.ShelterArea;
        var trigger = ShelterTriggerOf(f);
        if (trigger is ShelterTrigger.AwaitingEmergency or ShelterTrigger.NoIndoorBed)
        {
            return [];
        }
        var noKill = f.NoDangerArea;
        var window = f.DangerWindow;
        HashSet<PawnId> haulers = [.. f.DangerHaulers];
        HashSet<PawnId> combatants = [.. f.ShelterCombatants];
        var calm = f.Hostiles is HostileCensus.Quiet;
        var changes = new List<AllowedAreaChange>();

        void Add(PawnId id, bool animal, bool shelter, string area)
        {
            var dash = !animal && trigger == ShelterTrigger.Temperature && calm && f.DashOpenFor(id);
            shelter = shelter && !dash;
            var hauler = !animal && haulers.Contains(id);
            var to = "";
            var move = false;
            if (shelter && safe != "")
            {
                to = safe;
                move = area != safe;
            }
            else if (noKill != "" && window && hauler)
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
                move = !window || !hauler;
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

        var safety = f.RecoverySafety;
        foreach (var worker in f.MoodPawns)
        {
                if (worker.Dead || worker.Downed || worker.Drafted || worker.Mental)
                {
                    continue;
                }
                // A threat shelters only pawns known outside the draft set.
                var shelter = trigger != ShelterTrigger.None && (trigger != ShelterTrigger.Threat || !combatants.Contains(worker.Id));
                foreach (var restriction in safety.Restrictions)
                {
                    if (restriction.Pawn.Id == worker.Id.Value)
                    {
                        Add(worker.Id, false, shelter, restriction.AreaId ?? "");
                        break;
                    }
                }
        }
        foreach (var animal in f.AnimalUpkeep.Animals)
        {
            if (!animal.SupportsAreas || animal.RequiresPen)
            {
                continue;
            }
            Add(animal.Id, true, trigger != ShelterTrigger.None, animal.AllowedArea);
        }
        // Stable by pawn id.
        return changes.OrderBy(c => c.Pawn).ToList();
    }

    /// <summary>
    /// Whether a threat's sheltering response is complete: a threat triggers sheltering and every living,
    /// undowned, undrafted, sane colonist (at least one) is restricted to the Safe area. The clock run then
    /// watches the threat instead of refusing it: sheltered colonists wait it out on game time.
    /// </summary>
    public static bool ShelterHeld(ReviewFacts f)
    {
        var trigger = ShelterTriggerOf(f);
        var safe = f.ShelterArea;
        var safety = f.RecoverySafety;
        var workers = f.MoodPawns;
        if (trigger != ShelterTrigger.Threat || string.IsNullOrEmpty(safe))
        {
            return false;
        }
        var held = 0;
        foreach (var worker in workers)
        {
            if (worker.Dead || worker.Downed || worker.Mental || worker.Drafted)
            {
                continue;
            }
            var inside = false;
            foreach (var restriction in safety.Restrictions)
            {
                if (restriction.Pawn.Id == worker.Id.Value)
                {
                    inside = restriction.AreaId == safe;
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
