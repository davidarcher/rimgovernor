using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RimGovernor.Brain.Domain;
using BuildingHitPoints = RimGovernor.Brain.GameFacts.Observations.BuildingHitPoints;

namespace RimGovernor.Brain.Policy;

public sealed record RecoverySafety
{
    public EquatableArray<RimGovernor.Brain.GameFacts.Observations.RecoveryRestriction> Restrictions { get; init; }
}

public sealed record RecoveryPlanning
{
    public RecoverySafety Safety { get; init; } = new();

    public EquatableArray<MoodPawn> Workers { get; init; }

    public EquatableArray<RimGovernor.Brain.GameFacts.Observations.BuildingState> Buildings { get; init; }

    /// <summary>Throws when the planning facts are inconsistent.</summary>
    public void Validate()
    {
        DisasterRules.RecoveryPending(Buildings);
        var pawns = new HashSet<PawnId>();
        foreach (var r in Safety.Restrictions)
        {
            if (!FoodForecastRules.FoodId(r.Pawn.Id) || !pawns.Add(new PawnId(r.Pawn.Id)))
            {
                throw new DomainException("invalid recovery restriction pawn");
            }
            if (r.AreaId is { } area && !FoodForecastRules.FoodId(area))
            {
                throw new DomainException("invalid current restriction");
            }
        }
        var seen = new HashSet<PawnId>();
        foreach (var w in Workers)
        {
            if (!FoodForecastRules.FoodId(w.Id.Value) || !seen.Add(w.Id))
            {
                throw new DomainException("invalid recovery worker");
            }
        }
    }
}

public enum RecoveryProposalKind
{
    None = 0,
    [WireName("service_work")]
    ServiceWork,
}

public enum RecoverySelectionReason
{
    None = 0,
    [WireName("native_admission_required")]
    AdmissionRequired,
    [WireName("no_available_worker")]
    NoWorker,
    [WireName("methods_already_seen")]
    MethodsSeen,
    [WireName("tracked_infrastructure_missing_or_protected")]
    TrackedMissing,
    [WireName("no_pending_work")]
    NoWork,
}

/// <summary>
/// These are candidates for native preview, never admitted jobs. PriorArea is the pawn's observed
/// restriction; hazard sheltering is PlanSheltering's.
/// </summary>
public sealed record RecoveryCandidate
{
    public MethodId ID { get; init; }

    public RecoveryProposalKind Kind { get; init; }

    public PawnId Pawn { get; init; }

    public string Building { get; init; } = "";

    public RecoveryMethod Method { get; init; }

    /// <summary>The building's hit points when selected: part of the id, so a repair that changed them is a new candidate.</summary>
    public required BuildingHitPoints HitPoints { get; init; }

    /// <summary>The building's fuel when selected: part of the id, as the hit points are.</summary>
    public required RimGovernor.Brain.GameFacts.Observations.BuildingFuel Fuel { get; init; }

    public string? PriorArea { get; init; }

    /// <summary>The id: a hash of the candidate's JSON with the ID blanked.</summary>
    internal MethodId MethodIdOf()
    {
        var json = "{\"ID\":\"\",\"Kind\":" + GearRules.GoJsonString(Wire.Name(Kind))
            + ",\"Pawn\":" + GearRules.GoJsonString(Pawn.Value)
            + ",\"Building\":" + GearRules.GoJsonString(Building)
            + ",\"Method\":" + GearRules.GoJsonString(Wire.Name(Method))
            + ",\"HitPoints\":" + (HitPoints is BuildingHitPoints.Tracked h ? h.Current.ToString(CultureInfo.InvariantCulture) : "null")
            + ",\"Fuel\":" + (Fuel is RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable f ? RecoveryRules.GoJsonFloat(f.Fuel) : "null")
            + ",\"PriorArea\":" + (PriorArea is { } a ? GearRules.GoJsonString(a) : "null") + "}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return new MethodId("recovery-" + Convert.ToHexStringLower(hash.AsSpan(0, 16)));
    }
}

public sealed record RecoverySelection
{
    public Tick Tick { get; init; }

    public RecoverySelectionReason Reason { get; init; }

    public EquatableArray<RecoveryCandidate> Candidates { get; init; }

    /// <summary>Throws when the selection is inconsistent.</summary>
    public void Validate()
    {
        if (Tick.Value < 0 || Candidates.Count > 8)
        {
            throw new DomainException("invalid recovery selection bounds");
        }
        if (Reason == RecoverySelectionReason.None || !Wire.IsDefined(Reason))
        {
            throw new DomainException("invalid recovery selection reason");
        }
        if ((Candidates.Count > 0) != (Reason == RecoverySelectionReason.AdmissionRequired))
        {
            throw new DomainException("recovery reason contradicts candidates");
        }
        var seen = new HashSet<MethodId>();
        foreach (var c in Candidates)
        {
            if (!FoodForecastRules.FoodId(c.Pawn.Value) || c.ID != c.MethodIdOf() || !seen.Add(c.ID) || c.PriorArea is null
                || (c.PriorArea.Length != 0 && !FoodForecastRules.FoodId(c.PriorArea)))
            {
                throw new DomainException("invalid recovery candidate identity");
            }
            if (c.HitPoints is BuildingHitPoints.Tracked { Current: < 0 } || (c.Fuel is RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable fuel && !FoodForecastRules.FoodNumber(fuel.Fuel)))
            {
                throw new DomainException("invalid recovery candidate state");
            }
            if (c.Kind != RecoveryProposalKind.ServiceWork || !FoodForecastRules.FoodId(c.Building)
                || (c.Method != RecoveryMethod.Refuel && c.Method != RecoveryMethod.Breakdown && c.Method != RecoveryMethod.Repair)
                || (c.Method == RecoveryMethod.Refuel && c.Fuel is not RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable) || (c.Method == RecoveryMethod.Repair && c.HitPoints is not BuildingHitPoints.Tracked))
            {
                throw new DomainException("invalid recovery service proposal");
            }
        }
    }
}

public static partial class RecoveryRules
{
    /// <summary>The JSON float64 text: shortest round-trip digits, exponent form outside [1e-6, 1e21).</summary>
    internal static string GoJsonFloat(double d)
    {
        if (d == 0)
        {
            return double.IsNegative(d) ? "-0" : "0";
        }
        var r = Math.Abs(d).ToString("E16", CultureInfo.InvariantCulture);
        // Shortest digits: try increasing precision until the value round-trips.
        var digits = "";
        var exp = 0;
        for (var p = 0; p <= 16; p++)
        {
            var s = Math.Abs(d).ToString("E" + p.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            if (double.Parse(s, CultureInfo.InvariantCulture) == Math.Abs(d))
            {
                r = s;
                break;
            }
        }
        var ePos = r.IndexOf('E');
        digits = r[..ePos].Replace(".", "", StringComparison.Ordinal);
        exp = int.Parse(r[(ePos + 1)..], CultureInfo.InvariantCulture);
        var sign = d < 0 ? "-" : "";
        if (exp < -6 || exp >= 21)
        {
            var mantissa = digits.Length > 1 ? digits[0] + "." + digits[1..] : digits;
            var ex = Math.Abs(exp).ToString(CultureInfo.InvariantCulture);
            return sign + mantissa + "e" + (exp < 0 ? "-" : "+") + ex;
        }
        if (exp < 0)
        {
            return sign + "0." + new string('0', -exp - 1) + digits;
        }
        if (digits.Length <= exp + 1)
        {
            return sign + digits + new string('0', exp + 1 - digits.Length);
        }
        return sign + digits[..(exp + 1)] + "." + digits[(exp + 1)..];
    }

    public static Finding RecoveryNeed(DisasterHistory? h)
    {
        try
        {
            h?.Validate();
        }
        catch (DomainException)
        {
            return Finding.Unclear;
        }
        if (h is not null && h.Phase == DisasterPhase.Unread)
        {
            return Finding.Unclear;
        }
        if (h is null || h.Phase == DisasterPhase.Restored)
        {
            return Finding.Met;
        }
        return h.Services[h.Services.Count - 1].Finding;
    }

    /// <summary>
    /// Observes the current service job after an intent has been applied. Changing hit points or fuel must not
    /// create a replacement while that work is still running. This is derived from the census, not a second
    /// commitment ledger. Unknown jobs/targets cannot establish that work stopped.
    /// </summary>
    public static bool RecoveryWorkContinuing(RecoveryService service, EquatableArray<RimGovernor.Brain.GameFacts.Observations.BuildingState> buildings, WorkPawnsRead pawns)
    {
        var work = DisasterRules.RecoveryPending([.. buildings.Where(b => b.Building.Id == service.Thing)]);
        var needed = false;
        foreach (var need in work)
        {
            if (need.Building == service.Thing && Wire.Name(need.Method) == Wire.Name(service.Method))
            {
                needed = true;
            }
        }
        if (!needed)
        {
            return false;
        }
        var rows = pawns.Pawns;
        foreach (var pawn in rows)
        {
            if (!pawn.Id.Equals(service.Pawn))
            {
                continue;
            }
            var job = pawn.Job;
            var name = service.Method switch
            {
                RecoveryMethod.Repair => "Repair",
                RecoveryMethod.Breakdown => "FixBrokenDownBuilding",
                RecoveryMethod.Refuel => "Refuel",
                RecoveryMethod.InvestigateMonolith or RecoveryMethod.ActivateMonolith or RecoveryMethod.Interact
                    or RecoveryMethod.InspectGravEngine or RecoveryMethod.OfferHelp => null,
                RecoveryMethod invalid => throw new DomainException($"invalid recovery service method {invalid}"),
            };
            if (name is null || job.Def != name)
            {
                return false;
            }
            return job.Target.Thing == service.Thing;
        }
        return false;
    }

    /// <summary>
    /// Bounds the next admission batch to eight proposals. Seen methods belong to the current shared Episode,
    /// including retired plans. Merely creating or refreshing a batch does not count as attempting its methods.
    /// Throws on an invalid input.
    /// </summary>
    public static RecoverySelection SelectRecoveryMethods(RecoveryPlanning p, DisasterHistory? h, IReadOnlyList<MethodId> used, Tick tick)
    {
        var output = new RecoverySelection { Tick = tick, Reason = RecoverySelectionReason.NoWork };
        p.Validate();
        h?.Validate();
        if (tick.Value < 0 || (h is not null && (h.Observed > tick || (h.Phase != DisasterPhase.Restored && h.Observed != tick))))
        {
            throw new DomainException("invalid recovery selection boundary");
        }
        var seen = new HashSet<MethodId>();
        foreach (var id in used)
        {
            if (!FoodForecastRules.FoodId(id.Value) || !seen.Add(id))
            {
                throw new DomainException("invalid used recovery method");
            }
        }
        if (h is null || h.Phase == DisasterPhase.Restored)
        {
            return output with { Reason = RecoverySelectionReason.NoWork };
        }
        var buildings = p.Buildings;
        var safety = p.Safety;
        if (h.Phase == DisasterPhase.Unread)
        {
            throw new RequiredFactException("disaster service finding");
        }
        var workers = p.Workers;
        // The independent exact-pawn and restriction censuses must agree. Missing identities cannot silently
        // remove a pawn's exposure or player restriction.
        if (workers.Count != safety.Restrictions.Count)
        {
            throw new DomainException("recovery worker and restriction censuses disagree");
        }
        var byPawn = new Dictionary<PawnId, MoodPawn>();
        foreach (var w in workers)
        {
            byPawn[w.Id] = w;
        }
        foreach (var r in safety.Restrictions)
        {
            if (!byPawn.ContainsKey(new PawnId(r.Pawn.Id)))
            {
                throw new DomainException($"recovery restriction has no worker: {r.Pawn.Id}");
            }
        }
        var available = new List<MoodPawn>();
        foreach (var w in workers)
        {
            if (!w.Dead && !w.Downed && !w.Drafted && !w.Mental)
            {
                available.Add(w);
            }
        }
        // A player-forced job is costlier to interrupt than an ordinary one.
        available = available.OrderBy(w => w.PlayerForced ? 1 : 0).ThenBy(w => w.Id).ToList();
        if (available.Count == 0)
        {
            return output with { Reason = RecoverySelectionReason.NoWorker };
        }
        var candidates = new List<RecoveryCandidate>();
        void Add(RecoveryCandidate c)
        {
            if (candidates.Count >= 8)
            {
                return;
            }
            c = c with { ID = c.MethodIdOf() };
            if (!seen.Contains(c.ID))
            {
                candidates.Add(c);
            }
        }
        var restrictions = new Dictionary<PawnId, string>();
        foreach (var r in safety.Restrictions)
        {
            restrictions[new PawnId(r.Pawn.Id)] = r.AreaId ?? "";
        }
        var eligible = available;
        var work = DisasterRules.RecoveryPending(buildings);
        var byBuilding = buildings.ToDictionary(b => b.Building.Id, StringComparer.Ordinal);
        foreach (var w in eligible)
        {
            if (candidates.Count >= 8)
            {
                break;
            }
            foreach (var job in work)
            {
                if (candidates.Count >= 8)
                {
                    break;
                }
                var b = byBuilding[job.Building];
                Add(new RecoveryCandidate
                {
                    Kind = RecoveryProposalKind.ServiceWork,
                    Pawn = w.Id,
                    Building = job.Building,
                    Method = job.Method,
                    HitPoints = b.HitPoints,
                    Fuel = b.Service.Fuel,
                    PriorArea = restrictions[w.Id],
                });
            }
        }
        RecoverySelectionReason reason;
        if (candidates.Count > 0)
        {
            reason = RecoverySelectionReason.AdmissionRequired;
        }
        else if (work.Count > 0)
        {
            reason = RecoverySelectionReason.MethodsSeen;
        }
        else if (h.Services[h.Services.Count - 1].Finding != Finding.Met)
        {
            reason = RecoverySelectionReason.TrackedMissing;
        }
        else
        {
            reason = RecoverySelectionReason.NoWork;
        }
        return output with { Reason = reason, Candidates = EquatableArray.From(candidates) };
    }
}
