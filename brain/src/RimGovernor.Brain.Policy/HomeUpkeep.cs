namespace RimGovernor.Brain.Policy;

/// <summary>
/// The next clearance step the native evidence supports (#3253): the runtime reads the clearance census, the
/// recovery queue and the planned ground and builds it; the family only decides whether to propose it. The
/// MethodId names the step's first target, kind and size, so the same step continues its open Method.
/// </summary>
/// <param name="Method">The step's MethodId, for example <c>deconstruct-&lt;id&gt;-x3</c>.</param>
/// <param name="Swaps">The doors the step swaps for walls: each is proposed only when the maintenance choice picks its replacement.</param>
/// <param name="Actions">The step's actions under a minted plan id.</param>
public sealed record ClearanceCandidate(MethodId Method, EquatableArray<string> Swaps, Func<PlanId, IReadOnlyList<DomainAction>> Actions);

/// <summary>
/// The transition decisions (bound or compared, ServicingChoiceRules.Choose) the planned-ground clearance step made at the read of
/// <paramref name="Tick"/>: the clearance work binds them into the plan once the step's Method is committed (task 8.2).
/// </summary>
public sealed record ClearanceTransitions(Tick Tick, EquatableArray<TransitionDecision> Decisions)
{
    /// <summary>The Need's open removal targets the same read's census shows settled (TransitionBindingRules.SettledOpen, task 8.3).</summary>
    public EquatableArray<string> Settled { get; init; }
}

/// <summary>
/// The clearance census one native read returned: the rows that are not the player's own buildings and the
/// planned-ground clearance work of the same read (PlannedGroundWork): unplanned buildings and floors on the ground
/// of planned rooms not yet standing. Ground is empty on a read that did not derive the planned-ground work.
/// </summary>
public sealed record ClearanceCensus(EquatableArray<ClearanceTarget> Targets)
{
    public EquatableArray<string> Ground { get; init; }
}

/// <summary>
/// What the home-upkeep family reads: the damaged-structure census, the clearance census of the non-player rows,
/// the planned-ground work of the same read and the clearance step those support. Each section is a complete
/// native read; an unknown section cannot prove its old targets disappeared, while a known empty one can.
/// </summary>
public sealed record HomeUpkeepFacts
{
    /// <summary>The damaged-structure census of the same read; empty on a pass that does not review repairs.</summary>
    public EquatableArray<RepairStructure> Structures { get; init; }

    /// <summary>The clearance census of the same read; null when the pass did not read it, and the clearance Need then refuses.</summary>
    public ClearanceCensus? Clearance { get; init; }

    /// <summary>The next clearance step, or null when none is supported or every target of it is already designated.</summary>
    public ClearanceCandidate? Candidate { get; init; }

    /// <summary>The planned-ground step's transition decisions; null when the pass derived no planned-ground step.</summary>
    public ClearanceTransitions? Transitions { get; init; }

    /// <summary>
    /// The measured servicing options of the same read's servicing census (task 5.1), joined to its canonical building rows; null
    /// when the read carried no coupled forecast, and every repair, fix and replacement is then unassessed.
    /// </summary>
    public ServicingCensus? Servicing { get; init; }

    /// <summary>The servicing underway: the Need's open orders and the pawns' running repair and fix jobs, re-derived each review.</summary>
    public EquatableArray<ServicingUnderway> Underway { get; init; }

    /// <summary>The servicing orders the native refused in this world while the refusal budget refuses their Methods.</summary>
    public EquatableArray<RefusedServicing> Refused { get; init; }
}

/// <summary>
/// The home-upkeep family (#3253): essential repairs and clearance of home obstructions. It replaced the
/// MaintainEssentialRepairs and ClearHomeObstructions Concerns, their upkeep inspection and planners.
/// <list type="bullet">
/// <item><c>home-upkeep/repairs</c> (Wellbeing): no damaged or broken-down player building inside Home. Each deficient
/// building's repair, fix, replacement, retirement and deferral are compared (<see cref="ServicingChoiceRules"/>, task 5.2) and
/// the chosen repair or fix is a claimed Method (an exact Repair order, or a RecoveryService breakdown fix). The Need stays
/// unmet until fresh facts observe the building whole: an accepted order restores nothing by itself.</item>
/// <item><c>home-upkeep/clearance</c> (Development): no removable obstruction on home ground and no planned-ground
/// work left. Its Method is the next clearance step; independent steps run side by side and overlapping ones are
/// refused by their claims. A step that swaps doors for walls is proposed only when the maintenance choice picks the
/// replacement for every door it swaps.</item>
/// </list>
/// Clearance does not wait on repairs: the old hold only labelled the progress strip and never stopped the planner.
/// </summary>
public sealed class HomeUpkeepFamily(bool repairs = true, bool clearance = true) : IFamily<HomeUpkeepFacts>
{
    public const string FamilyId = "home-upkeep";

    public static readonly NeedId Repairs = NeedId.Of(FamilyId, "repairs");

    public static readonly NeedId Clearance = NeedId.Of(FamilyId, "clearance");

    /// <summary>The hold subjects when no work is chosen: a deferral (its reasons in the detail), work already underway, or targets clearance serves.</summary>
    public const string Deferred = "servicing_deferred", Underway = "servicing_underway", ByClearance = "servicing_by_clearance";

    /// <summary>The MethodId prefixes of a repair and a breakdown fix: <c>repair-&lt;target&gt;-&lt;pawn&gt;</c>.</summary>
    public const string RepairMethod = "repair-", FixMethod = "fix-";

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Colony, FactFamily.Rooms, FactFamily.Emergency];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(HomeUpkeepFacts facts)
    {
        var needs = new List<Need>(2);
        if (repairs)
        {
            needs.Add(RepairsNeed(facts.Structures));
        }
        if (clearance)
        {
            needs.Add(ClearanceNeed(facts.Clearance));
        }
        return needs;
    }

    public IReadOnlyList<Need> DeriveSeriousHarm(HomeUpkeepFacts facts) => [];

    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, HomeUpkeepFacts facts)
    {
        var output = new List<NeedMethod>();
        var decisions = Decide(facts);
        foreach (var need in ready)
        {
            if (need.Id == Repairs)
            {
                var (targets, _) = DamagedStructures(facts.Structures);
                output.AddRange(RepairMethods(facts, [.. decisions.Where(d => targets.Contains(d.Target))]));
            }
            else if (need.Id == Clearance && facts.Candidate is { } step)
            {
                var swaps = decisions.Where(d => step.Swaps.Contains(d.Target)).ToArray();
                output.Add(step.Swaps.Count == 0 || (swaps.Length == step.Swaps.Count && swaps.All(d => d.Chosen.Kind == ServicingChoiceKind.Replace))
                    ? new NeedMethod(Clearance, step.Method, "", id => new PlanSpec(id, new PlanRevision(1), step.Actions(id)))
                    : NeedMethod.Held(Clearance, Deferred, "Door-to-wall replacement not chosen: " + string.Join("; ",
                        step.Swaps.Select(door => swaps.FirstOrDefault(d => d.Target == door) is { } d ? ServicingChoiceRules.Describe(d) : door + ": building row unread")) + "."));
            }
        }
        return output;
    }

    /// <summary>
    /// The maintenance choice over the deficient home buildings (most urgent first) and the doors the clearance step swaps: the
    /// clearance targets are the buildings the plan does not need (unknown while the clearance census is unread).
    /// </summary>
    public static IReadOnlyList<ServicingDecision> Decide(HomeUpkeepFacts facts)
    {
        var (targets, _) = DamagedStructures(facts.Structures);
        var rows = facts.Structures.ToDictionary(s => s.Id, s => s.Building, StringComparer.Ordinal);
        var swaps = facts.Candidate?.Swaps.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        var order = targets.Concat(swaps.Order(StringComparer.Ordinal).Where(s => !targets.Contains(s) && rows.ContainsKey(s))).ToArray();
        return ServicingChoiceRules.Decide(new ServicingRequest
        {
            Targets = [.. order.Select(id => rows[id])],
            Census = facts.Servicing,
            Swaps = swaps,
            Unneeded = facts.Clearance is { } census ? ClearanceTargets(census.Targets, census.Ground).ToHashSet(StringComparer.Ordinal) : null,
            Underway = facts.Underway,
            Refused = facts.Refused,
        });
    }

    /// <summary>
    /// The Repairs Need's Methods: each chosen repair or fix not already running natively, as an exact order claiming its target
    /// and pawn (an issued order continues its open Method). With none, one hold naming why: a deferral's exclusions, the work
    /// underway, or the targets clearance retires or replaces.
    /// </summary>
    private static IEnumerable<NeedMethod> RepairMethods(HomeUpkeepFacts facts, IReadOnlyList<ServicingDecision> decisions)
    {
        var cells = facts.Structures.ToDictionary(s => s.Id, s => s.Cell, StringComparer.Ordinal);
        var bound = 0;
        foreach (var d in decisions)
        {
            if (d.Underway is { Issued: false }) continue;
            var pawn = new PawnId(d.Chosen.Pawn);
            if (d.Chosen.Kind == ServicingChoiceKind.Repair)
            {
                bound++;
                yield return new NeedMethod(Repairs, new MethodId(RepairMethod + d.Target + "-" + d.Chosen.Pawn), ServicingChoiceRules.Describe(d),
                    id => new PlanSpec(id, new PlanRevision(1), [DomainAction.NewRepairAction(new ActionId(id.Value + "-0"), new Repair(pawn, d.Target, cells[d.Target]))]));
            }
            else if (d.Chosen.Kind == ServicingChoiceKind.FixBreakdown)
            {
                bound++;
                yield return new NeedMethod(Repairs, new MethodId(FixMethod + d.Target + "-" + d.Chosen.Pawn), ServicingChoiceRules.Describe(d),
                    id => new PlanSpec(id, new PlanRevision(1), [DomainAction.NewRecoveryServiceAction(new ActionId(id.Value + "-0"), new RecoveryService(pawn, d.Target, RecoveryMethod.Breakdown))]));
            }
        }
        if (bound > 0 || decisions.Count == 0) yield break;
        var subject = decisions.Any(d => d.Underway is null && d.Chosen.Kind == ServicingChoiceKind.Defer) ? Deferred
            : decisions.Any(d => d.Underway is not null) ? Underway : ByClearance;
        yield return NeedMethod.Held(Repairs, subject, string.Join("; ", decisions.Select(ServicingChoiceRules.Describe)) + ".");
    }

    /// <summary>
    /// The damaged or broken-down player buildings inside Home, most urgent first (repair priority, then lowest health
    /// fraction, then id), and their missing hit points. Throws on an invalid row.
    /// </summary>
    public static (IReadOnlyList<string> Targets, double Missing) DamagedStructures(IReadOnlyList<RepairStructure> structures)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<RepairStructure>();
        foreach (var row in structures)
        {
            var s = row;
            if (!FoodForecastRules.FoodId(s.Id) || !seen.Add(s.Id) || s.HitPoints.Current < 0 || s.HitPoints.Maximum < s.HitPoints.Current || row.Priority < 0 || row.Priority > 2 || s.Cell.X < 0 || s.Cell.Z < 0)
            {
                throw new DomainException("invalid upkeep structure");
            }
            if (s.Home && s.HitPoints.Maximum > 0 && (s.HitPoints.Current < s.HitPoints.Maximum || s.Building.Service.BrokenDown))
            {
                selected.Add(row);
            }
        }
        selected.Sort((a, b) =>
        {
            if (a.Priority != b.Priority)
            {
                return a.Priority.CompareTo(b.Priority);
            }
            var x = (double)a.HitPoints.Current / a.HitPoints.Maximum;
            var y = (double)b.HitPoints.Current / b.HitPoints.Maximum;
            return x != y ? (x < y ? -1 : 1) : string.CompareOrdinal(a.Id, b.Id);
        });
        var missing = selected.Sum(r => (double)(r.HitPoints.Maximum - r.HitPoints.Current));
        if (!double.IsFinite(missing))
        {
            throw new DomainException("upkeep metric overflow");
        }
        return ([.. selected.Select(r => r.Id)], missing);
    }

    /// <summary>
    /// The clearance targets: every census row nothing about the thing itself refuses (<see cref="ClearanceRules.ClearanceHoldReason"/>),
    /// then the planned-ground work. Throws on an invalid or repeated id.
    /// </summary>
    public static IReadOnlyList<string> ClearanceTargets(IReadOnlyList<ClearanceTarget> rows, EquatableArray<string> ground)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<string>();
        foreach (var row in rows)
        {
            if (!FoodForecastRules.FoodId(row.EntityId) || !seen.Add(row.EntityId))
            {
                throw new DomainException("invalid clearance target");
            }
            if (ClearanceRules.ClearanceHoldReason(row).Length == 0)
            {
                selected.Add(row.EntityId);
            }
        }
        foreach (var id in ground)
        {
            if (!FoodForecastRules.FoodId(id) || !seen.Add(id))
            {
                throw new DomainException("invalid planned ground target");
            }
            selected.Add(id);
        }
        return selected;
    }

    private static Need RepairsNeed(EquatableArray<RepairStructure> census)
    {
        // Each breakdown counts one beside the missing hit points: the Need is met only on a fresh read of whole buildings.
        var (targets, missing) = DamagedStructures(census);
        var broken = census.Count(s => targets.Contains(s.Id) && s.Building.Service.BrokenDown);
        return new Need
        {
            Id = Repairs,
            Tier = NeedTier.Wellbeing,
            Satisfaction = targets.Count == 0 ? Satisfaction.Met : Satisfaction.Unmet,
            Deficit = missing + broken,
        };
    }

    private static Need ClearanceNeed(ClearanceCensus? census)
    {
        if (census is null)
        {
            throw new RequiredFactException("clearance census");
        }
        var targets = ClearanceTargets(census.Targets, census.Ground);
        return new Need
        {
            Id = Clearance,
            Tier = NeedTier.Development,
            Satisfaction = targets.Count == 0 ? Satisfaction.Met : Satisfaction.Unmet,
            Deficit = (double)targets.Count,
        };
    }

}
