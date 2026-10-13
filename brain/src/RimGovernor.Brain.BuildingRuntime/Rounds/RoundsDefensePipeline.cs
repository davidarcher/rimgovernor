namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The perimeter methods still open in the defense standard, the cells their buildings occupy and the buildings not yet standing.</summary>
internal sealed class DefensePipeline
{
    public List<MethodId> Open = [];
    public HashSet<Cell> Cells = [];
    public List<Building> Unbuilt = [];
    public int Admitted;
    public Identity Scope = new();
    public bool Scoped;

    public bool Piped() => Open.Count > 0 || Admitted > 0;

    public bool Overlaps(IEnumerable<Building> buildings) => buildings.Any(b => Cells.Contains(b.Cell));

    public void Add(IEnumerable<Building> buildings)
    {
        foreach (var b in buildings)
        {
            Cells.Add(b.Cell);
        }
    }

    public void Settle(DefenseCensus? census)
    {
        if (census is null)
        {
            // Go dereferences the census here; an absent census leaves the unbuilt list as it was.
            return;
        }
        Unbuilt = Unbuilt.Where(b => !census.Standing(b.Definition, b.Cell)).ToList();
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>Whether an open method belongs to the tier: its id is the tier prefix, an optional repair marker and the attempt number.</summary>
    internal static bool DefenseTierOpen(IEnumerable<MethodId> open, string tier)
    {
        var prefix = RoundsDefenseLayoutPlanner.DefenseTierPrefix(tier);
        foreach (var method in open)
        {
            if (!method.Value.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            var rest = method.Value[prefix.Length..];
            if (rest.StartsWith('r'))
            {
                var repair = rest[1..];
                var dash = repair.IndexOf('-', StringComparison.Ordinal);
                if (dash >= 0)
                {
                    rest = repair[(dash + 1)..];
                }
            }
            if (rest != "" && rest.All(char.IsAsciiDigit))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The open defense methods of the standard; not pipelinable when an open plan is anything but a perimeter building plan.</summary>
    internal static (DefensePipeline Pipe, bool Ok) DefenseOpenTiers(StandardState goal, Func<PlanId, PlanState> plans)
    {
        var pipe = new DefensePipeline();
        foreach (var method in goal.Methods)
        {
            var plan = plans(method.Plan);
            if (!StorePackage.PlanOpen(plan))
            {
                continue;
            }
            if (!method.MethodId.Value.StartsWith("defense-" + PolicyConstants.TierPerimeterPrefix, StringComparison.Ordinal))
            {
                return (new DefensePipeline(), false);
            }
            foreach (var action in plan.Spec.Actions)
            {
                if (action.Building is not { } b)
                {
                    return (new DefensePipeline(), false);
                }
                pipe.Cells.Add(b.Cell);
                pipe.Unbuilt.Add(b);
            }
            pipe.Open.Add(method.MethodId);
        }
        return (pipe, true);
    }

    internal static void DefenseAction(string component, TelemetryLevel level, string verdict, string reason, string target, IReadOnlyDictionary<string, object?>? attrs, CancellationToken ct)
    {
        Tel.Decide(new Decision { Kind = "defense_action", Component = component, Level = level, Verdict = verdict, Reason = reason, Target = target, Attrs = attrs ?? new Dictionary<string, object?>() });
    }

    internal static void DefenseTierGated(string tier, string gate, CancellationToken ct)
    {
        if (gate == "")
        {
            return;
        }
        DefenseAction("defense-layout", TelemetryLevel.Info, "refused", gate, tier, null, ct);
    }

    internal static void DefenseSnapshotSkip(string component, string target, Exception error, CancellationToken ct)
    {
        Tel.Decide(new Decision
        {
            Kind = "snapshot_skip",
            Component = component,
            Level = TelemetryLevel.Warn,
            Verdict = "skipped",
            Reason = "not_recorded",
            Target = target,
            Attrs = new Dictionary<string, object?> { ["error"] = error },
        });
    }
}
