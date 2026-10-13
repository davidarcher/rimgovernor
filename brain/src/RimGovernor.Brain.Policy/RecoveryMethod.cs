using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RimGovernor.Brain.Domain;
using DomainRecoveryMethod = RimGovernor.Brain.Domain.RecoveryMethod;

namespace RimGovernor.Brain.Policy;

public sealed record RecoveryRestriction(PawnId Pawn, Fact<string> Area);

public sealed record RecoverySafety
{
    public EquatableArray<RecoveryRestriction> Restrictions { get; init; }
}

public sealed record RecoveryWorker
{
    public PawnId Pawn { get; init; }

    public Fact<bool> Dead { get; init; }

    public Fact<bool> Downed { get; init; }

    public Fact<bool> Drafted { get; init; }

    public Fact<bool> Mental { get; init; }

    public Fact<bool> PlayerForced { get; init; }
}

public sealed record RecoveryPlanning
{
    public Fact<RecoverySafety> Safety { get; init; }

    public Fact<EquatableArray<RecoveryWorker>> Workers { get; init; }

    public Fact<EquatableArray<RecoveryBuilding>> Buildings { get; init; }

    /// <summary>Throws when the planning facts are inconsistent.</summary>
    public void Validate()
    {
        PolicyFunctions.RecoveryPending(Buildings);
        if (Safety.TryGetValue(out var s))
        {
            var pawns = new HashSet<PawnId>();
            foreach (var r in s.Restrictions)
            {
                if (!PolicyFunctions.FoodId(r.Pawn.Value) || !pawns.Add(r.Pawn))
                {
                    throw new DomainException("invalid recovery restriction pawn");
                }
                if (r.Area.TryGetValue(out var id) && id.Length != 0 && !PolicyFunctions.FoodId(id))
                {
                    throw new DomainException("invalid current restriction");
                }
            }
        }
        if (Workers.TryGetValue(out var workers))
        {
            var seen = new HashSet<PawnId>();
            foreach (var w in workers)
            {
                if (!PolicyFunctions.FoodId(w.Pawn.Value) || !seen.Add(w.Pawn))
                {
                    throw new DomainException("invalid recovery worker");
                }
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
    [WireName("observations_unknown")]
    FactsUnknown,
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

    public long? HitPoints { get; init; }

    public double? Fuel { get; init; }

    public string? PriorArea { get; init; }

    /// <summary>The id as Go derived it: a hash of the candidate's JSON with the ID blanked.</summary>
    internal MethodId MethodIdOf()
    {
        var json = "{\"ID\":\"\",\"Kind\":" + PolicyFunctions.GoJsonString(Wire.Name(Kind))
            + ",\"Pawn\":" + PolicyFunctions.GoJsonString(Pawn.Value)
            + ",\"Building\":" + PolicyFunctions.GoJsonString(Building)
            + ",\"Method\":" + PolicyFunctions.GoJsonString(Wire.Name(Method))
            + ",\"HitPoints\":" + (HitPoints is { } h ? h.ToString(CultureInfo.InvariantCulture) : "null")
            + ",\"Fuel\":" + (Fuel is { } f ? PolicyFunctions.GoJsonFloat(f) : "null")
            + ",\"PriorArea\":" + (PriorArea is { } a ? PolicyFunctions.GoJsonString(a) : "null") + "}";
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
        if (Reason == RecoverySelectionReason.None)
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
            if (!PolicyFunctions.FoodId(c.Pawn.Value) || c.ID != c.MethodIdOf() || !seen.Add(c.ID) || c.PriorArea is null
                || (c.PriorArea.Length != 0 && !PolicyFunctions.FoodId(c.PriorArea)))
            {
                throw new DomainException("invalid recovery candidate identity");
            }
            if ((c.HitPoints is < 0) || (c.Fuel is { } fuel && !PolicyFunctions.FoodNumber(fuel)))
            {
                throw new DomainException("invalid recovery candidate state");
            }
            if (c.Kind != RecoveryProposalKind.ServiceWork || !PolicyFunctions.FoodId(c.Building)
                || (c.Method != RecoveryMethod.Refuel && c.Method != RecoveryMethod.Breakdown && c.Method != RecoveryMethod.Repair)
                || (c.Method == RecoveryMethod.Refuel && c.Fuel is null) || (c.Method == RecoveryMethod.Repair && c.HitPoints is null))
            {
                throw new DomainException("invalid recovery service proposal");
            }
        }
    }
}

public static partial class PolicyFunctions
{
    /// <summary>Go's json.Marshal float64 text: shortest round-trip digits, exponent form outside [1e-6, 1e21).</summary>
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
        if (h is not null && h.Phase == DisasterPhase.Unknown)
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
    public static Fact<bool> RecoveryWorkContinuing(RecoveryService service, Fact<EquatableArray<RecoveryBuilding>> buildings, Fact<EquatableArray<WorkPawn>> pawns)
    {
        if (!buildings.TryGetValue(out var observed))
        {
            return Unknown<bool>();
        }
        var targetBuildings = observed.Where(b => b.Id == service.Thing).ToList();
        Fact<EquatableArray<RecoveryWork>> pending;
        try
        {
            pending = RecoveryPending(Known(EquatableArray.From(targetBuildings)));
        }
        catch (DomainException)
        {
            return Unknown<bool>();
        }
        if (!pending.TryGetValue(out var work))
        {
            return Unknown<bool>();
        }
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
            return Known(false);
        }
        if (!pawns.TryGetValue(out var rows))
        {
            return Unknown<bool>();
        }
        foreach (var pawn in rows)
        {
            if (!pawn.Id.Equals(service.Pawn))
            {
                continue;
            }
            if (!pawn.Job.TryGetValue(out var job))
            {
                return Unknown<bool>();
            }
            var name = service.Method switch
            {
                DomainRecoveryMethod.Repair => "Repair",
                DomainRecoveryMethod.Breakdown => "FixBrokenDownBuilding",
                DomainRecoveryMethod.Refuel => "Refuel",
                _ => "",
            };
            if (job.Def != name)
            {
                return Known(false);
            }
            if (!job.Target.TryGetValue(out var target))
            {
                return Unknown<bool>();
            }
            return Known(target.Thing == service.Thing);
        }
        return Known(false);
    }

    /// <summary>
    /// Bounds the next admission batch to eight proposals. Seen methods belong to the current shared Episode,
    /// including retired plans. Merely creating or refreshing a batch does not count as attempting its methods.
    /// Throws on an invalid input.
    /// </summary>
    public static RecoverySelection SelectRecoveryMethods(RecoveryPlanning p, DisasterHistory? h, IReadOnlyList<MethodId> used, Tick tick)
    {
        var output = new RecoverySelection { Tick = tick, Reason = RecoverySelectionReason.FactsUnknown };
        p.Validate();
        h?.Validate();
        if (tick.Value < 0 || (h is not null && (h.Observed > tick || (h.Phase != DisasterPhase.Restored && h.Observed != tick))))
        {
            throw new DomainException("invalid recovery selection boundary");
        }
        var seen = new HashSet<MethodId>();
        foreach (var id in used)
        {
            if (!FoodId(id.Value) || !seen.Add(id))
            {
                throw new DomainException("invalid used recovery method");
            }
        }
        if (h is null || h.Phase == DisasterPhase.Restored)
        {
            return output with { Reason = RecoverySelectionReason.NoWork };
        }
        var pending = RecoveryPending(p.Buildings);
        var wk = pending.TryGetValue(out var work);
        var sk = p.Safety.TryGetValue(out var safety);
        var pk = p.Workers.TryGetValue(out var workers);
        if (!sk || !pk || h.Phase == DisasterPhase.Unknown)
        {
            return output;
        }
        // The independent exact-pawn and restriction censuses must agree. Missing identities cannot silently
        // remove a pawn's exposure or player restriction.
        if (workers.Count != safety!.Restrictions.Count)
        {
            return output;
        }
        var byPawn = new Dictionary<PawnId, RecoveryWorker>();
        foreach (var w in workers)
        {
            byPawn[w.Pawn] = w;
        }
        foreach (var r in safety.Restrictions)
        {
            if (!byPawn.ContainsKey(r.Pawn))
            {
                return output;
            }
        }
        var available = new List<RecoveryWorker>();
        var unknownWorker = false;
        foreach (var w in workers)
        {
            bool blocked = false, known = true;
            foreach (var f in new[] { w.Dead, w.Downed, w.Drafted, w.Mental })
            {
                var k = f.TryGetValue(out var v);
                blocked = blocked || (k && v);
                known = known && k;
            }
            if (!blocked && !known)
            {
                unknownWorker = true;
            }
            if (!blocked && known)
            {
                available.Add(w);
            }
        }
        available = available.OrderBy(w => OrderedWorkCost(w.PlayerForced)).ThenBy(w => w.Pawn).ToList();
        if (available.Count == 0)
        {
            return unknownWorker ? output : output with { Reason = RecoverySelectionReason.NoWorker };
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
        var restrictions = new Dictionary<PawnId, Fact<string>>();
        foreach (var r in safety.Restrictions)
        {
            restrictions[r.Pawn] = r.Area;
        }
        var eligible = new List<RecoveryWorker>();
        var areaUnknown = false;
        foreach (var w in available)
        {
            if (!restrictions[w.Pawn].TryGetValue(out _))
            {
                areaUnknown = true;
                continue;
            }
            eligible.Add(w);
        }
        if (areaUnknown || unknownWorker || !wk)
        {
            return output;
        }
        var byBuilding = new Dictionary<string, RecoveryBuilding>(StringComparer.Ordinal);
        p.Buildings.TryGetValue(out var buildings);
        foreach (var b in buildings)
        {
            byBuilding[b.Id] = b;
        }
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
                var b = byBuilding.GetValueOrDefault(job.Building) ?? new RecoveryBuilding();
                restrictions[w.Pawn].TryGetValue(out var prior);
                Add(new RecoveryCandidate
                {
                    Kind = RecoveryProposalKind.ServiceWork,
                    Pawn = w.Pawn,
                    Building = job.Building,
                    Method = job.Method,
                    HitPoints = b.HitPoints.TryGetValue(out var hp) ? hp : null,
                    Fuel = b.Fuel.TryGetValue(out var fuel) ? fuel : null,
                    PriorArea = prior ?? "",
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
