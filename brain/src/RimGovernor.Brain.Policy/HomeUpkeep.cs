using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The next clearance step the native evidence supports (#3253): the runtime reads the clearance census, the
/// recovery queue and the planned ground and builds it; the family only decides whether to propose it. The
/// MethodId names the step's first target, kind and size, so the same step continues its open Method.
/// </summary>
/// <param name="Method">The step's MethodId, for example <c>deconstruct-&lt;id&gt;-x3</c>.</param>
/// <param name="ReplacesWithWall">Whether the step swaps a door for a wall (door-to-wall labor is not yet a supported forecast outcome).</param>
/// <param name="Actions">The step's actions under a minted plan id.</param>
public sealed record ClearanceCandidate(MethodId Method, bool ReplacesWithWall, Func<PlanId, IReadOnlyList<DomainAction>> Actions);

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
}

/// <summary>
/// The home-upkeep family (#3253): essential repairs and clearance of home obstructions. It replaced the
/// MaintainEssentialRepairs and ClearHomeObstructions Concerns, their upkeep inspection and planners.
/// <list type="bullet">
/// <item><c>home-upkeep/repairs</c> (Wellbeing): no damaged player building inside Home. Repairs are not yet a
/// supported forecast outcome, so an unmet Need is held as <c>unsupported_forecast_repair</c>.</item>
/// <item><c>home-upkeep/clearance</c> (Development): no removable obstruction on home ground and no planned-ground
/// work left. Its Method is the next clearance step; independent steps run side by side and overlapping ones are
/// refused by their claims.</item>
/// </list>
/// Clearance does not wait on repairs: the old hold only labelled the progress strip and never stopped the planner,
/// and repairs proposes no work it could wait for.
/// </summary>
public sealed class HomeUpkeepFamily(bool repairs = true, bool clearance = true) : IFamily<HomeUpkeepFacts>
{
    public const string FamilyId = "home-upkeep";

    public static readonly NeedId Repairs = NeedId.Of(FamilyId, "repairs");

    public static readonly NeedId Clearance = NeedId.Of(FamilyId, "clearance");

    /// <summary>The stable refusal subject of an unmet repairs Need.</summary>
    public const string UnsupportedRepair = "unsupported_forecast_repair";

    /// <summary>The stable refusal subject of a clearance step that swaps a door for a wall.</summary>
    public const string UnsupportedWallReplacement = "unsupported_forecast_wall_replacement";

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
        foreach (var need in ready)
        {
            if (need.Id == Repairs)
            {
                output.Add(NeedMethod.Held(Repairs, UnsupportedRepair,
                    "Observed repair demand requires shared forecast worker, timing and outcome alternatives."));
            }
            else if (need.Id == Clearance && facts.Candidate is { } step)
            {
                output.Add(step.ReplacesWithWall
                    ? NeedMethod.Held(Clearance, UnsupportedWallReplacement, "Door-to-wall labor requires shared observed worker and construction obligations.")
                    : new NeedMethod(Clearance, step.Method, "", id => new PlanSpec(id, new PlanRevision(1), step.Actions(id))));
            }
        }
        return output;
    }

    /// <summary>
    /// The damaged player buildings inside Home, most urgent first (repair priority, then lowest health fraction,
    /// then id), and their missing hit points. Throws on an invalid row.
    /// </summary>
    public static (IReadOnlyList<string> Targets, double Missing) DamagedStructures(IReadOnlyList<RepairStructure> structures)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<RepairStructure>();
        foreach (var row in structures)
        {
            var s = row.Structure;
            if (!FoodForecastRules.FoodId(s.Id) || !seen.Add(s.Id) || s.HitPoints < 0 || s.MaxHitPoints < s.HitPoints || row.Priority < 0 || row.Priority > 2 || s.Cell.X < 0 || s.Cell.Z < 0)
            {
                throw new DomainException("invalid upkeep structure");
            }
            if (s.Home && s.MaxHitPoints > 0 && s.HitPoints < s.MaxHitPoints)
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
            var x = (double)a.Structure.HitPoints / a.Structure.MaxHitPoints;
            var y = (double)b.Structure.HitPoints / b.Structure.MaxHitPoints;
            return x != y ? (x < y ? -1 : 1) : string.CompareOrdinal(a.Structure.Id, b.Structure.Id);
        });
        var missing = selected.Sum(r => (double)(r.Structure.MaxHitPoints - r.Structure.HitPoints));
        if (!double.IsFinite(missing))
        {
            throw new DomainException("upkeep metric overflow");
        }
        return ([.. selected.Select(r => r.Structure.Id)], missing);
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
        var (targets, missing) = DamagedStructures(census);
        return new Need
        {
            Id = Repairs,
            Tier = NeedTier.Wellbeing,
            Satisfaction = targets.Count == 0 ? Satisfaction.Met : Satisfaction.Unmet,
            Deficit = missing,
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
