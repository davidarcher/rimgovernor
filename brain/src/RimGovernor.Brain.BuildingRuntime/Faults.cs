using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The acceptance harness's fault injections into a live scheduler: a named catalog planner that fails every
/// step, and one that never returns (blocks until its step context ends). They show which failures leave safe
/// play running (an optional planner) and which stop it (a critical planner held past the wall budget). A staged
/// start may also pin the food plan's gap to zero, standing in for a food economy the fixture does not build.
/// Never set in ordinary play. The epoch renewal fault (<c>renewal=drop</c>) is dropped with the native clock
/// lease: the native clock stays in the mod.
/// </summary>
public sealed record Faults
{
    /// <summary>The environment variable the host reads faults from.</summary>
    public const string FaultsEnv = "RIMGOVERNOR_FAULT_INJECT";

    /// <summary>Catalog planners whose run throws <see cref="FaultInjectedException"/> at once, before any native read.</summary>
    public IReadOnlySet<string> FailPlanners { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Catalog planners whose run blocks until the step (critical) or wave (optional) context ends.</summary>
    public IReadOnlySet<string> HangPlanners { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Pins every food plan's GapPerDay to zero, so the gates that wait on a closed food gap (MaintainHousing's expansion) open.</summary>
    public bool FoodGapZero { get; init; }

    /// <summary>
    /// Raises the stock target the station chain sizes the storage group for (stationNeeds), so a standing
    /// storage room is outgrown and the tiled replan sizes a successor; no planner reads it as a target to acquire.
    /// </summary>
    public IReadOnlyDictionary<Resource, long> StockDemand { get; init; } = new Dictionary<Resource, long>();

    /// <summary>
    /// Reads the <see cref="FaultsEnv"/> format: a semicolon-separated list of <c>planner:&lt;name&gt;=fail</c>,
    /// <c>planner:&lt;name&gt;=hang</c>, <c>foodgap=zero</c> and <c>stock=&lt;Resource&gt;:&lt;count&gt;</c> (repeatable).
    /// An empty string is no fault. Throws <see cref="FormatException"/> for anything else.
    /// </summary>
    public static Faults Parse(string raw)
    {
        var fail = new HashSet<string>(StringComparer.Ordinal);
        var hang = new HashSet<string>(StringComparer.Ordinal);
        var stock = new Dictionary<Resource, long>();
        var foodGapZero = false;
        foreach (var rawItem in raw.Split(';'))
        {
            var item = rawItem.Trim();
            if (item.Length == 0)
            {
                continue;
            }
            var cut = item.IndexOf('=', StringComparison.Ordinal);
            if (cut < 0)
            {
                throw new FormatException($"{FaultsEnv}: \"{item}\" is not key=value");
            }
            var key = item[..cut];
            var value = item[(cut + 1)..];
            if (key == "foodgap" && value == "zero")
            {
                foodGapZero = true;
            }
            else if (key == "stock")
            {
                var colon = value.IndexOf(':', StringComparison.Ordinal);
                var def = colon < 0 ? value : value[..colon];
                var count = colon < 0 ? "" : value[(colon + 1)..];
                if (colon < 0 || def.Length == 0 || !long.TryParse(count, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) || n <= 0)
                {
                    throw new FormatException($"{FaultsEnv}: stock fault \"{value}\" is not Resource:count");
                }
                stock[new Resource(def)] = n;
            }
            else if (key.StartsWith("planner:", StringComparison.Ordinal))
            {
                var name = key["planner:".Length..];
                if (name.Length == 0)
                {
                    throw new FormatException($"{FaultsEnv}: \"{item}\" names no planner");
                }
                switch (value)
                {
                    case "fail":
                        fail.Add(name);
                        break;
                    case "hang":
                        hang.Add(name);
                        break;
                    default:
                        throw new FormatException($"{FaultsEnv}: planner fault \"{value}\" is not fail or hang");
                }
            }
            else
            {
                throw new FormatException($"{FaultsEnv}: unknown fault \"{item}\"");
            }
        }
        return new Faults { FailPlanners = fail, HangPlanners = hang, FoodGapZero = foodGapZero, StockDemand = stock };
    }

    /// <summary>Whether no fault is configured.</summary>
    public bool Empty => FailPlanners.Count == 0 && HangPlanners.Count == 0 && !FoodGapZero && StockDemand.Count == 0;

    /// <summary>Renders the faults in <see cref="FaultsEnv"/>'s format, for the startup log.</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        parts.AddRange(FailPlanners.Order(StringComparer.Ordinal).Select(name => "planner:" + name + "=fail"));
        parts.AddRange(HangPlanners.Order(StringComparer.Ordinal).Select(name => "planner:" + name + "=hang"));
        if (FoodGapZero)
        {
            parts.Add("foodgap=zero");
        }
        foreach (var def in StockDemand.Keys.OrderBy(k => k.Value, StringComparer.Ordinal))
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"stock={def.Value}:{StockDemand[def]}"));
        }
        return string.Join(';', parts);
    }

    /// <summary>Refuses a planner name the catalog does not know, so a typo never passes for a clean run.</summary>
    public void Validate()
    {
        var known = ClockPlannerCatalog.Entries.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var names in new[] { FailPlanners, HangPlanners })
        {
            foreach (var name in names)
            {
                if (!known.Contains(name))
                {
                    throw new FormatException($"{FaultsEnv}: no catalog planner named \"{name}\"");
                }
            }
        }
    }

    /// <summary>
    /// Wraps <paramref name="run"/> with the fault configured for the named planner, if any: a failing planner
    /// throws at once, a hanging one waits out <paramref name="ct"/>.
    /// </summary>
    public Func<Task> PlannerFault(string name, CancellationToken ct, Func<Task> run)
    {
        if (FailPlanners.Contains(name))
        {
            return () => throw new FaultInjectedException($"{name} fails every step");
        }
        if (HangPlanners.Contains(name))
        {
            return async () =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException e)
                {
                    throw new FaultInjectedException($"{name} hung until the context ended", e);
                }
            };
        }
        return run;
    }
}

/// <summary>Go <c>errFaultInjected</c>.</summary>
public sealed class FaultInjectedException : Exception
{
    public FaultInjectedException(string message)
        : base($"fault injected: {message}") { }

    public FaultInjectedException(string message, Exception inner)
        : base($"fault injected: {message}", inner) { }
}
