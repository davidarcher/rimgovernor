namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The perimeter methods still open in the defense standard, the cells their buildings occupy and the buildings not yet standing.</summary>
internal sealed class DefensePipeline
{
    public List<MethodId> Open = [];
    public HashSet<Cell> Cells = [];
    public List<Building> Unbuilt = [];
    public int Admitted;
    public Identity Scope;
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
            if (!method.Method.Value.StartsWith("defense-" + PolicyApi.TierPerimeterPrefix, StringComparison.Ordinal))
            {
                return (new DefensePipeline(), false);
            }
            foreach (var action in plan.Spec.Actions)
            {
                if (!action.Building(out var b))
                {
                    return (new DefensePipeline(), false);
                }
                pipe.Cells.Add(b.Cell);
                pipe.Unbuilt.Add(b);
            }
            pipe.Open.Add(method.Method);
        }
        return (pipe, true);
    }

    internal static void DefenseAction(CancellationToken ct, string component, TelemetryLevel level, string verdict, string reason, string target, IReadOnlyDictionary<string, object?>? attrs)
    {
        Tel.Decide(ct, new Decision { Kind = "defense_action", Component = component, Level = level, Verdict = verdict, Reason = reason, Target = target, Attrs = attrs });
    }

    internal static void DefenseTierGated(CancellationToken ct, string tier, string gate)
    {
        if (gate == "")
        {
            return;
        }
        DefenseAction(ct, "defense-layout", LogLevel.Info, "refused", gate, tier, null);
    }

    internal static void DefenseSnapshotSkip(CancellationToken ct, string component, string target, Exception error)
    {
        Tel.Decide(ct, new Decision
        {
            Kind = "snapshot_skip",
            Component = component,
            Level = LogLevel.Warn,
            Verdict = "skipped",
            Reason = "not_recorded",
            Target = target,
            Attrs = new Dictionary<string, object?> { ["error"] = error },
        });
    }
}
