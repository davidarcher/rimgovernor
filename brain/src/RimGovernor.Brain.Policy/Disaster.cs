namespace RimGovernor.Brain.Policy;

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
    [WireName("unknown")]
    Unknown,
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
/// One native game condition affecting the colony map. TicksLeft is the native remaining duration when the
/// condition is timed and that read was available; it is null for a permanent condition or when native did not
/// report one. Remaining time is planning evidence only: an episode ends when the condition is no longer
/// observed, never when the count runs out.
/// </summary>
public sealed record DisasterCondition
{
    public string Id { get; init; } = "";

    public string Definition { get; init; } = "";

    public bool Permanent { get; init; }

    public long? TicksLeft { get; init; }

    /// <summary>The game's ElectricityDisabled for the condition: its def's class is GameCondition_DisableElectricity or derives from it.</summary>
    public bool DisablesPower { get; init; }

    /// <summary>Reports the observed remaining duration when known.</summary>
    public Fact<long> RemainingTicks() => TicksLeft is { } t ? Known(t) : Unknown<long>();
}

public sealed record DisasterEvidence(DisasterService Service, Finding Finding);

public enum RecoveryMethod
{
    [WireName("refuel")]
    Refuel,
    [WireName("breakdown")]
    Breakdown,
    [WireName("repair")]
    Repair,
}

public sealed record RecoveryWork(string Building, RecoveryMethod Method);

public sealed record RecoveryBuilding
{
    public string Id { get; init; } = "";

    public Fact<bool> UsesHitPoints { get; init; }

    public Fact<bool> Broken { get; init; }

    public Fact<bool> Forbidden { get; init; }

    public Fact<bool> Burning { get; init; }

    public Fact<long> HitPoints { get; init; }

    public Fact<long> MaxHitPoints { get; init; }

    public Fact<double> Fuel { get; init; }

    public Fact<double> FuelTarget { get; init; }

    public Fact<bool> Refuelable { get; init; }
}

/// <summary>Observed disruption evidence. It grants neither orders nor recovery from elapsed time, disappeared buildings or an expired game condition.</summary>
public sealed record DisasterHistory
{
    public long? ShortCircuitTick { get; init; }

    public bool WorkKnown { get; init; }

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
        var all = PolicyFunctions.DisasterServices;
        if (Started.Value < 0 || Observed < Started || Services.Count != all.Count || Affected.Count > all.Count)
        {
            throw new DomainException("invalid disaster history bounds");
        }
        if (ShortCircuitTick is { } sc && (sc < 0 || sc > Observed.Value))
        {
            throw new DomainException("invalid short circuit history");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Conditions)
        {
            if (!PolicyFunctions.FoodId(c.Id) || !PolicyFunctions.FoodId(c.Definition) || seen.Contains(c.Id) || (c.TicksLeft is { } left && (c.Permanent || left < 0)))
            {
                throw new DomainException("invalid disaster condition");
            }
            seen.Add(c.Id);
        }
        seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in Damaged)
        {
            if (!PolicyFunctions.FoodId(id) || !seen.Add(id))
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
        bool deficit = false, unknown = false;
        foreach (var e in Services)
        {
            deficit = deficit || e.Finding == Finding.Unmet;
            unknown = unknown || e.Finding == Finding.Unclear;
            if (e.Finding == Finding.Unmet && !services.Contains(e.Service))
            {
                throw new DomainException("untracked disaster deficit");
            }
        }
        if (!WorkKnown && Work.Count > 0)
        {
            throw new DomainException("invalid recovery work evidence");
        }
        var workSeen = new HashSet<RecoveryWork>();
        foreach (var w in Work)
        {
            if (!PolicyFunctions.FoodId(w.Building) || !workSeen.Add(w))
            {
                throw new DomainException("invalid recovery work method");
            }
        }
        if (Phase != DisasterPhase.Unknown
            && ((unknown && !deficit)
                || ((Phase == DisasterPhase.Disrupted || Phase == DisasterPhase.Recovering) != deficit)
                || ((Phase == DisasterPhase.Disrupted || Phase == DisasterPhase.Survival) != (Conditions.Count > 0))))
        {
            throw new DomainException("disaster phase contradicts evidence");
        }
    }

    /// <summary>The priority a concern takes while an unmet disaster service names it as its goal.</summary>
    public int Promote(ConcernId id, int priority)
    {
        if (Phase != DisasterPhase.Disrupted && Phase != DisasterPhase.Recovering)
        {
            return priority;
        }
        foreach (var e in Services)
        {
            if (e.Finding != Finding.Unmet)
            {
                continue;
            }
            var goal = e.Service switch
            {
                DisasterService.Food => PolicyFunctions.EnsureFoodSupply,
                DisasterService.Production => PolicyFunctions.EnsureFoodSupply,
                DisasterService.Sleeping => PolicyFunctions.MaintainHousing,
                DisasterService.Shelter => PolicyFunctions.MaintainHousing,
                DisasterService.Temperature => PolicyFunctions.EnsureTemperatureSafety,
                DisasterService.Cooking => PolicyFunctions.EnsureCooking,
                DisasterService.Power => PolicyFunctions.EnsureBasicPower,
                DisasterService.Storage => PolicyFunctions.MaintainFoodStorage,
                _ => PolicyFunctions.RecoverDisasterServices,
            };
            if (goal == id || (id == PolicyFunctions.MaintainResource && (e.Service == DisasterService.Temperature || e.Service == DisasterService.Cooking)))
            {
                return Math.Min(priority, 2);
            }
        }
        return priority;
    }
}

public static partial class PolicyFunctions
{
    public static readonly ConcernId RecoverDisasterServices = new("RecoverDisasterServices");

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
    /// The longest observed remaining duration of any listed condition definition that is active with a native
    /// TicksLeft read: zero when the census is known and no listed condition carries one, unknown while the
    /// condition census itself is unknown. A listed condition without a remaining-duration read contributes
    /// nothing: the reviews that consult this plan against a duration, never against the bare presence of a condition.
    /// </summary>
    public static Fact<long> ConditionRemainingTicks(Fact<EquatableArray<DisasterCondition>> conditions, params string[] definitions) =>
        ConditionRemaining(conditions, c => definitions.Contains(c.Definition));

    private static Fact<long> ConditionRemaining(Fact<EquatableArray<DisasterCondition>> conditions, Func<DisasterCondition, bool> match)
    {
        if (!conditions.TryGetValue(out var rows))
        {
            return Unknown<long>();
        }
        long longest = 0;
        foreach (var c in rows)
        {
            if (!match(c))
            {
                continue;
            }
            if (c.RemainingTicks().TryGetValue(out var ticks))
            {
                longest = Math.Max(longest, ticks);
            }
        }
        return Known(longest);
    }

    /// <summary>
    /// Reports an active condition that disables electricity (a solar flare) with a known remaining duration:
    /// every powered building is off until it ends, so power and refrigeration development is suspended (the
    /// goals stay open with no method) rather than answered with generators or coolers for an outage measured in hours.
    /// </summary>
    public static bool PowerOutageHold(Fact<EquatableArray<DisasterCondition>> conditions) =>
        ConditionRemaining(conditions, c => c.DisablesPower).TryGetValue(out var ticks) && ticks > 0;

    /// <summary>
    /// Reports an active eclipse with a known remaining duration: the sky gives no light by day for as long as
    /// it lasts, so an unroofed work cell measured dark is a lighting deficit a torch answers rather than the
    /// nightly flap the roofed-only census avoids.
    /// </summary>
    public static bool EclipseHold(Fact<EquatableArray<DisasterCondition>> conditions) =>
        ConditionRemainingTicks(conditions, ConditionEclipse).TryGetValue(out var ticks) && ticks > 0;

    /// <summary>
    /// Reports that the sky gives no light by day: an eclipse (see EclipseHold) or a biome whose map conditions
    /// black the sky out for good (outdoorsDark). It is unknown while outdoorsDark is: an unread biome is never assumed lit.
    /// </summary>
    public static Fact<bool> SkyDarkHold(Fact<EquatableArray<DisasterCondition>> conditions, Fact<bool> outdoorsDark)
    {
        if (!outdoorsDark.TryGetValue(out var dark))
        {
            return Unknown<bool>();
        }
        return Known(dark || EclipseHold(conditions));
    }

    /// <summary>
    /// Lists measured ordinary work. Native eligibility and resource previews remain mandatory at action
    /// admission; protected buildings are skipped. Throws on an invalid row.
    /// </summary>
    public static Fact<EquatableArray<RecoveryWork>> RecoveryPending(Fact<EquatableArray<RecoveryBuilding>> observed)
    {
        if (!observed.TryGetValue(out var rows))
        {
            return Unknown<EquatableArray<RecoveryWork>>();
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var work = new List<RecoveryWork>();
        var complete = true;
        foreach (var row in rows)
        {
            if (!FoodId(row.Id) || !seen.Add(row.Id))
            {
                throw new DomainException("invalid recovery identity");
            }
            foreach (var f in new[] { row.Fuel, row.FuelTarget })
            {
                if (f.TryGetValue(out var v) && !FoodNumber(v))
                {
                    throw new DomainException("invalid recovery fuel");
                }
            }
            foreach (var f in new[] { row.HitPoints, row.MaxHitPoints })
            {
                if (f.TryGetValue(out var v) && v < 0)
                {
                    throw new DomainException("invalid recovery hit points");
                }
            }
            var hk = row.HitPoints.TryGetValue(out var hp);
            var mk = row.MaxHitPoints.TryGetValue(out var maximum);
            if (hk && mk && (maximum <= 0 || hp > maximum))
            {
                throw new DomainException("inconsistent recovery hit points");
            }
            var fk = row.Forbidden.TryGetValue(out var forbidden);
            var bk = row.Burning.TryGetValue(out var burning);
            if (forbidden || burning)
            {
                continue;
            }
            if (!fk || !bk)
            {
                complete = false;
                continue;
            }
            var brokenKnown = row.Broken.TryGetValue(out var broken);
            var uk = row.UsesHitPoints.TryGetValue(out var uses);
            var rk = row.Refuelable.TryGetValue(out var refuel);
            complete = complete && brokenKnown && uk && rk;
            if (broken)
            {
                work.Add(new RecoveryWork(row.Id, RecoveryMethod.Breakdown));
            }
            if (uk && uses)
            {
                if (!hk || !mk)
                {
                    complete = false;
                }
                else if (hp < maximum)
                {
                    work.Add(new RecoveryWork(row.Id, RecoveryMethod.Repair));
                }
            }
            if (rk && refuel)
            {
                var fuelKnown = row.Fuel.TryGetValue(out var fuel);
                var tk = row.FuelTarget.TryGetValue(out var target);
                if (!fuelKnown || !tk)
                {
                    complete = false;
                }
                else if (fuel < target * .25)
                {
                    work.Add(new RecoveryWork(row.Id, RecoveryMethod.Refuel));
                }
            }
        }
        if (!complete)
        {
            return Unknown<EquatableArray<RecoveryWork>>();
        }
        var sorted = work
            .OrderByDescending(w => w.Method == RecoveryMethod.Refuel)
            .ThenBy(w => w.Building, StringComparer.Ordinal)
            .ToList();
        return Known(EquatableArray.From(sorted));
    }

    /// <summary>
    /// Uses the same survival gates as routine planning. The store resets history on world replacement or rewind
    /// and retains it through Manual. Throws on an invalid observation.
    /// </summary>
    public static DisasterHistory? ReviewDisaster(
        Fact<EquatableArray<DisasterCondition>> conditions,
        Fact<EquatableArray<RecoveryBuilding>> buildings,
        IReadOnlyDictionary<DisasterService, Fact<bool>> services,
        DisasterHistory? previous,
        Tick tick,
        params Fact<Tick>[] shortCircuit)
    {
        previous?.Validate();
        if (tick.Value < 0 || (previous is not null && tick < previous.Observed))
        {
            throw new DomainException("stale disaster observation");
        }
        if (shortCircuit.Length > 1)
        {
            throw new DomainException("too many short circuit observations");
        }
        var incident = new Tick(0);
        var newIncident = false;
        if (shortCircuit.Length == 1)
        {
            var knownIncident = shortCircuit[0].TryGetValue(out incident);
            if (knownIncident && (incident.Value < 0 || incident > tick))
            {
                throw new DomainException("invalid short circuit observation");
            }
            newIncident = knownIncident && (previous is null || previous.ShortCircuitTick is null || incident.Value > previous.ShortCircuitTick);
        }
        var workFact = RecoveryPending(buildings);
        var known = conditions.TryGetValue(out var current);
        var currentRows = known ? current : EquatableArray.From(new List<DisasterCondition>());
        var seenConditions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in currentRows)
        {
            if (!FoodId(c.Id) || !FoodId(c.Definition) || !seenConditions.Add(c.Id))
            {
                throw new DomainException("invalid environmental condition");
            }
            if (c.TicksLeft is { } left && (c.Permanent || left < 0))
            {
                throw new DomainException("invalid environmental condition duration");
            }
        }
        var h = previous;
        if (!known)
        {
            if (h is not null && h.Phase != DisasterPhase.Restored)
            {
                h = h with { Phase = DisasterPhase.Unknown, Observed = tick };
            }
            return h;
        }
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
        var shortCircuitTick = h.ShortCircuitTick;
        if (newIncident)
        {
            shortCircuitTick = incident.Value;
        }
        if (shortCircuitTick is null && previous is not null)
        {
            shortCircuitTick = previous.ShortCircuitTick;
        }
        var tracked = new HashSet<string>(h.Damaged, StringComparer.Ordinal);
        if (newIncident)
        {
            if (buildings.TryGetValue(out var rows))
            {
                foreach (var b in rows)
                {
                    var hk = b.HitPoints.TryGetValue(out var hp);
                    var mk = b.MaxHitPoints.TryGetValue(out var maximum);
                    var bk = b.Broken.TryGetValue(out var broken);
                    if ((hk && mk && hp < maximum) || (bk && broken))
                    {
                        tracked.Add(b.Id);
                    }
                }
            }
        }
        var complete = workFact.TryGetValue(out var pending);
        var pendingList = complete ? pending.ToList() : [];
        foreach (var w in pendingList)
        {
            if (w.Method != RecoveryMethod.Refuel)
            {
                tracked.Add(w.Building);
            }
        }
        var infrastructure = Unknown<bool>();
        if (complete)
        {
            var recovered = pendingList.Count == 0;
            var observedOk = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (buildings.TryGetValue(out var rows))
            {
                foreach (var b in rows)
                {
                    var uk = b.UsesHitPoints.TryGetValue(out var uses);
                    var hk = b.HitPoints.TryGetValue(out var hp);
                    var mk = b.MaxHitPoints.TryGetValue(out var maximum);
                    var bk = b.Broken.TryGetValue(out var broken);
                    observedOk[b.Id] = uk && (!uses || (hk && mk && hp == maximum)) && bk && !broken;
                }
            }
            foreach (var id in tracked)
            {
                recovered = recovered && observedOk.GetValueOrDefault(id);
            }
            infrastructure = Known(recovered);
        }
        var facts = DisasterServices.Select(s => services.GetValueOrDefault(s)).ToList();
        facts[^1] = infrastructure;
        var evidence = new List<DisasterEvidence>();
        var affected = new HashSet<DisasterService>(h.Affected);
        bool deficit = false, unknown = false;
        for (var i = 0; i < facts.Count; i++)
        {
            var need = Finding.Unclear;
            if (facts[i].TryGetValue(out var value))
            {
                need = Finding.Met;
                if (!value)
                {
                    need = Finding.Unmet;
                    deficit = true;
                    affected.Add(DisasterServices[i]);
                }
            }
            else
            {
                unknown = true;
            }
            evidence.Add(new DisasterEvidence(DisasterServices[i], need));
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
        else if (unknown)
        {
            phase = DisasterPhase.Unknown;
        }
        else if (currentRows.Count > 0)
        {
            phase = DisasterPhase.Survival;
        }
        var result = h with
        {
            ShortCircuitTick = shortCircuitTick,
            Observed = tick,
            Conditions = EquatableArray.From(currentRows.OrderBy(c => c.Id, StringComparer.Ordinal)),
            WorkKnown = complete,
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
