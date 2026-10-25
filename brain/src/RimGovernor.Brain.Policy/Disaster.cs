using O = RimGovernor.Contracts.Wire.Observations;
using BuildingHitPoints = RimGovernor.Contracts.Wire.Observations.BuildingHitPoints;
using ConditionDuration = RimGovernor.Contracts.Wire.Observations.ConditionDuration;

namespace RimGovernor.Brain.Policy;

/// <summary>A survival service observed this review; food awaits the first food plan.</summary>
public enum DisasterServiceStatus { Restored, Deficit, AwaitingFoodPlan }


public enum DisasterPhase
{
    [WireName("disrupted")]
    Disrupted,
    [WireName("temporary_survival")]
    Survival,
    [WireName("recovering")]
    Recovering,
    [WireName("restored")]
    Restored,

    /// <summary>
    /// The review could not judge the episode: a survival service's finding was unread with no deficit standing. The disaster/services Need raises <see cref="RequiredFactException"/> on it and
    /// the service selection plans nothing.
    /// </summary>
    [WireName("unread")]
    Unread,
}

public enum DisasterService
{
    [WireName("food")]
    Food,
    [WireName("production")]
    Production,
    [WireName("sleeping")]
    Sleeping,
    [WireName("shelter")]
    Shelter,
    [WireName("temperature")]
    Temperature,
    [WireName("cooking")]
    Cooking,
    [WireName("power")]
    Power,
    [WireName("storage")]
    Storage,
    [WireName("infrastructure")]
    Infrastructure,
}

/// <summary>
/// One native game condition affecting the colony map: permanent, or timed with the native remaining duration.
/// Remaining time is planning evidence only: an episode ends when the condition is no longer observed, never when
/// the count runs out.
/// </summary>
public sealed record DisasterCondition
{
    public required O.EnvironmentCondition Observed { get; init; }

    public string Id => Observed.Id;

    public string Definition => Observed.DefName;

    public ConditionDuration Duration => Observed.Duration;

    /// <summary>The game's ElectricityDisabled for the condition: its def's class is GameCondition_DisableElectricity or derives from it.</summary>
    public bool DisablesPower { get; init; }
}

public sealed record DisasterEvidence(DisasterService Service, Finding Finding);

public sealed record RecoveryWork(string Building, RecoveryMethod Method);

/// <summary>The latest short circuit a disaster history has seen: none yet, or the tick it happened.</summary>
public abstract record DisasterShortCircuit
{
    private protected DisasterShortCircuit()
    {
    }

    public sealed record NoneSeen : DisasterShortCircuit;

    public sealed record Seen(Tick Tick) : DisasterShortCircuit;
}

/// <summary>Observed disruption evidence. It grants neither orders nor recovery from elapsed time, disappeared buildings or an expired game condition.</summary>
public sealed record DisasterHistory
{
    public DisasterShortCircuit ShortCircuit { get; init; } = new DisasterShortCircuit.NoneSeen();

    public EquatableArray<RecoveryWork> Work { get; init; }

    public Tick Started { get; init; }

    public Tick Observed { get; init; }

    public DisasterPhase Phase { get; init; }

    public EquatableArray<DisasterCondition> Conditions { get; init; }

    public EquatableArray<DisasterEvidence> Services { get; init; }

    public EquatableArray<DisasterService> Affected { get; init; }

    public EquatableArray<string> Damaged { get; init; }

    /// <summary>Throws when the history is inconsistent.</summary>
    public void Validate()
    {
        var all = DisasterRules.DisasterServices;
        if (Started.Value < 0 || Observed < Started || Services.Count != all.Count || Affected.Count > all.Count)
        {
            throw new DomainException("invalid disaster history bounds");
        }
        if (ShortCircuit is DisasterShortCircuit.Seen { Tick: var sc } && (sc.Value < 0 || sc > Observed))
        {
            throw new DomainException("invalid short circuit history");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Conditions)
        {
            if (!FoodForecastRules.FoodId(c.Id) || !FoodForecastRules.FoodId(c.Definition) || seen.Contains(c.Id) || c.Duration is ConditionDuration.Timed { Ticks: < 0 })
            {
                throw new DomainException("invalid disaster condition");
            }
            seen.Add(c.Id);
        }
        seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in Damaged)
        {
            if (!FoodForecastRules.FoodId(id) || !seen.Add(id))
            {
                throw new DomainException("invalid damaged building history");
            }
        }
        for (var i = 0; i < Services.Count; i++)
        {
            var e = Services[i];
            if (e.Service != all[i] || (e.Finding != Finding.Unclear && e.Finding != Finding.Unmet && e.Finding != Finding.Met))
            {
                throw new DomainException("invalid disaster service evidence");
            }
        }
        var services = new HashSet<DisasterService>();
        foreach (var s in Affected)
        {
            if (!all.Contains(s) || !services.Add(s))
            {
                throw new DomainException("invalid affected service");
            }
        }
        bool deficit = false, unread = false;
        foreach (var e in Services)
        {
            deficit = deficit || e.Finding == Finding.Unmet;
            unread = unread || e.Finding == Finding.Unclear;
            if (e.Finding == Finding.Unmet && !services.Contains(e.Service))
            {
                throw new DomainException("untracked disaster deficit");
            }
        }
        var workSeen = new HashSet<RecoveryWork>();
        foreach (var w in Work)
        {
            if (!FoodForecastRules.FoodId(w.Building) || !workSeen.Add(w))
            {
                throw new DomainException("invalid recovery work method");
            }
        }
        if (Phase != DisasterPhase.Unread
            && ((unread && !deficit)
                || ((Phase == DisasterPhase.Disrupted || Phase == DisasterPhase.Recovering) != deficit)
                || ((Phase == DisasterPhase.Disrupted || Phase == DisasterPhase.Survival) != (Conditions.Count > 0))))
        {
            throw new DomainException("disaster phase contradicts evidence");
        }
    }
}

public static class DisasterRules
{
    internal static readonly IReadOnlyList<DisasterService> DisasterServices =
    [
        DisasterService.Food, DisasterService.Production, DisasterService.Sleeping, DisasterService.Shelter,
        DisasterService.Temperature, DisasterService.Cooking, DisasterService.Power, DisasterService.Storage,
        DisasterService.Infrastructure,
    ];

    // Native game condition definitions the rounds consult by name.
    public const string ConditionEclipse = "Eclipse";
    public const string ConditionPsychicDrone = "PsychicDrone";
    public const string ConditionVolcanicWinter = "VolcanicWinter";
    public const string ConditionColdSnap = "ColdSnap";
    public const string ConditionHeatWave = "HeatWave";
    public const string ConditionToxicFallout = "ToxicFallout";

    /// <summary>
    /// The longest remaining duration of any listed condition definition that is active and timed: zero when no
    /// listed condition is timed. A permanent listed condition contributes nothing: the reviews that consult this
    /// plan against a duration, never against the bare presence of a condition.
    /// </summary>
    public static long ConditionRemainingTicks(EquatableArray<DisasterCondition> conditions, params string[] definitions) =>
        ConditionRemaining(conditions, c => definitions.Contains(c.Definition));

    private static long ConditionRemaining(EquatableArray<DisasterCondition> conditions, Func<DisasterCondition, bool> match)
    {
        long longest = 0;
        foreach (var c in conditions)
        {
            if (match(c) && c.Duration is ConditionDuration.Timed { Ticks: var ticks })
            {
                longest = Math.Max(longest, ticks);
            }
        }
        return longest;
    }

    /// <summary>
    /// Reports an active timed condition that disables electricity (a solar flare): every powered building is off
    /// until it ends, so power and refrigeration development is suspended (the goals stay open with no method)
    /// rather than answered with generators or coolers for an outage measured in hours.
    /// </summary>
    public static bool PowerOutageHold(EquatableArray<DisasterCondition> conditions) =>
        ConditionRemaining(conditions, c => c.DisablesPower) > 0;

    /// <summary>
    /// Reports an active timed eclipse: the sky gives no light by day for as long as it lasts, so an unroofed work
    /// cell measured dark is a lighting deficit a torch answers rather than the nightly flap the roofed-only census avoids.
    /// </summary>
    public static bool EclipseHold(EquatableArray<DisasterCondition> conditions) =>
        ConditionRemainingTicks(conditions, ConditionEclipse) > 0;

    /// <summary>
    /// Reports that the sky gives no light by day: an eclipse (see EclipseHold) or a biome whose map conditions
    /// black the sky out for good (outdoorsDark).
    /// </summary>
    public static bool SkyDarkHold(EquatableArray<DisasterCondition> conditions, bool outdoorsDark) =>
        outdoorsDark || EclipseHold(conditions);

    /// <summary>
    /// Lists measured ordinary work: a broken-down building, a building below its maximum hit points and a
    /// refuelable building under a quarter of its fuel target, refuels first. Forbidden and burning buildings are
    /// skipped. Native eligibility and resource previews remain mandatory at action admission. Throws on an invalid row.
    /// </summary>
    public static EquatableArray<RecoveryWork> RecoveryPending(IReadOnlyList<O.BuildingState> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var work = new List<RecoveryWork>();
        foreach (var row in rows)
        {
            if (!FoodForecastRules.FoodId(row.Building.Id) || !seen.Add(row.Building.Id))
            {
                throw new DomainException("invalid recovery identity");
            }
            if (row.Service.Fuel is O.BuildingFuel.Refuelable fuel && (!FoodForecastRules.FoodNumber(fuel.Fuel) || !FoodForecastRules.FoodNumber(fuel.TargetFuel)))
            {
                throw new DomainException("invalid recovery fuel");
            }
            if (row.HitPoints is BuildingHitPoints.Tracked hp && (hp.Current < 0 || hp.Maximum <= 0 || hp.Current > hp.Maximum))
            {
                throw new DomainException("inconsistent recovery hit points");
            }
            if (row.Settings.Forbidden || row.Burning)
            {
                continue;
            }
            if (row.Service.BrokenDown)
            {
                work.Add(new RecoveryWork(row.Building.Id, RecoveryMethod.Breakdown));
            }
            if (row.HitPoints is BuildingHitPoints.Tracked damaged && damaged.Current < damaged.Maximum)
            {
                work.Add(new RecoveryWork(row.Building.Id, RecoveryMethod.Repair));
            }
            if (row.Service.Fuel is O.BuildingFuel.Refuelable low && low.Fuel < low.TargetFuel * .25)
            {
                work.Add(new RecoveryWork(row.Building.Id, RecoveryMethod.Refuel));
            }
        }
        var sorted = work
            .OrderByDescending(w => w.Method == RecoveryMethod.Refuel)
            .ThenBy(w => w.Building, StringComparer.Ordinal)
            .ToList();
        return EquatableArray.From(sorted);
    }

    /// <summary>Whether a building stands whole: at its maximum hit points (or untracked) and not broken down.</summary>
    private static bool Whole(O.BuildingState b) =>
        (b.HitPoints is not BuildingHitPoints.Tracked hp || hp.Current == hp.Maximum) && !b.Service.BrokenDown;

    /// <summary>
    /// Uses the same survival gates as routine planning. The store resets history on world replacement or rewind
    /// and retains it through Manual. Throws on an invalid observation.
    /// </summary>
    private static Finding ServiceFinding(DisasterServiceStatus status, DisasterService service)
    {
        if (status == DisasterServiceStatus.Restored) return Finding.Met;
        if (status == DisasterServiceStatus.Deficit) return Finding.Unmet;
        if (status == DisasterServiceStatus.AwaitingFoodPlan && service == DisasterService.Food) return Finding.Unclear;
        throw new DomainException("invalid disaster service status");
    }

    public static DisasterHistory? ReviewDisaster(
        EquatableArray<DisasterCondition> currentRows,
        EquatableArray<O.BuildingState> rows,
        IReadOnlyDictionary<DisasterService, DisasterServiceStatus> services,
        DisasterHistory? previous,
        Tick tick,
        DisasterShortCircuit shortCircuit)
    {
        previous?.Validate();
        if (tick.Value < 0 || (previous is not null && tick < previous.Observed))
        {
            throw new DomainException("stale disaster observation");
        }
        var newIncident = false;
        var incident = new Tick(0);
        if (shortCircuit is DisasterShortCircuit.Seen { Tick: var seenTick })
        {
            incident = seenTick;
            if (incident.Value < 0 || incident > tick)
            {
                throw new DomainException("invalid short circuit observation");
            }
            newIncident = previous?.ShortCircuit is not DisasterShortCircuit.Seen { Tick: var last } || incident > last;
        }
        var pendingList = RecoveryPending(rows).ToList();
        var seenConditions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in currentRows)
        {
            if (!FoodForecastRules.FoodId(c.Id) || !FoodForecastRules.FoodId(c.Definition) || !seenConditions.Add(c.Id))
            {
                throw new DomainException("invalid environmental condition");
            }
            if (c.Duration is ConditionDuration.Timed { Ticks: < 0 })
            {
                throw new DomainException("invalid environmental condition duration");
            }
        }
        var h = previous;
        if (h is not null && h.Phase == DisasterPhase.Restored)
        {
            if (currentRows.Count == 0 && !newIncident)
            {
                return h;
            }
            h = null;
        }
        if (h is null)
        {
            if (currentRows.Count == 0 && !newIncident)
            {
                return null;
            }
            h = new DisasterHistory { Started = tick };
        }
        var shortCircuitSeen = h.ShortCircuit;
        if (newIncident)
        {
            shortCircuitSeen = new DisasterShortCircuit.Seen(incident);
        }
        if (shortCircuitSeen is DisasterShortCircuit.NoneSeen && previous is not null)
        {
            shortCircuitSeen = previous.ShortCircuit;
        }
        var tracked = new HashSet<string>(h.Damaged, StringComparer.Ordinal);
        if (newIncident)
        {
            foreach (var b in rows)
            {
                if (!Whole(b))
                {
                    tracked.Add(b.Building.Id);
                }
            }
        }
        foreach (var w in pendingList)
        {
            if (w.Method != RecoveryMethod.Refuel)
            {
                tracked.Add(w.Building);
            }
        }
        var whole = rows.ToDictionary(b => b.Building.Id, Whole, StringComparer.Ordinal);
        var recovered = pendingList.Count == 0 && tracked.All(id => whole.GetValueOrDefault(id));
        var infrastructure = recovered ? Finding.Met : Finding.Unmet;
        var evidence = new List<DisasterEvidence>();
        var affected = new HashSet<DisasterService>(h.Affected);
        bool deficit = false, unread = false;
        foreach (var service in DisasterServices)
        {
            var need = service == DisasterService.Infrastructure
                ? infrastructure
                : ServiceFinding(services[service], service);
            if (need == Finding.Unmet)
            {
                deficit = true;
                affected.Add(service);
            }
            unread = unread || need == Finding.Unclear;
            evidence.Add(new DisasterEvidence(service, need));
        }
        var phase = DisasterPhase.Restored;
        if (currentRows.Count > 0 && deficit)
        {
            phase = DisasterPhase.Disrupted;
        }
        else if (deficit)
        {
            phase = DisasterPhase.Recovering;
        }
        else if (unread)
        {
            phase = DisasterPhase.Unread;
        }
        else if (currentRows.Count > 0)
        {
            phase = DisasterPhase.Survival;
        }
        var result = h with
        {
            ShortCircuit = shortCircuitSeen,
            Observed = tick,
            Conditions = EquatableArray.From(currentRows.OrderBy(c => c.Id, StringComparer.Ordinal)),
            Work = EquatableArray.From(pendingList),
            Damaged = EquatableArray.From(tracked.OrderBy(id => id, StringComparer.Ordinal)),
            Services = EquatableArray.From(evidence),
            Affected = EquatableArray.From(DisasterServices.Where(affected.Contains)),
            Phase = phase,
        };
        result.Validate();
        return result;
    }
}
