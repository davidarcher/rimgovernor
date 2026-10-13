namespace RimGovernor.Brain.Simulation.Supply;

/// <summary>What a shock does to a source or a stock.</summary>
public enum ShockKind
{
    /// <summary>Multiplies a source's capacity by Factor.</summary>
    ScaleCapacity,

    /// <summary>
    /// Removes the Factor fraction (0..1) of a source's stock (a crop's planted cells, a herd's animals), or
    /// of a world good when Source is empty.
    /// </summary>
    DestroyStock,

    /// <summary>Ends a source for good.</summary>
    RemoveSource,

    /// <summary>Stops regeneration and crop growth for Days days.</summary>
    PauseGrowth,

    /// <summary>Stops a source delivering for Days days (caravan absent).</summary>
    StopWork,

    /// <summary>Blocks Safe sources (loot, salvage) for Days days.</summary>
    Threat,

    /// <summary>Cuts World.Power to the Factor fraction for Days days.</summary>
    PowerLoss,

    /// <summary>Caps Good's stock at Factor units for Days days; deliveries beyond the headroom are not made.</summary>
    StorageFull,

    /// <summary>Multiplies the Good's demand by Factor for Days days.</summary>
    SurgeDemand,

    /// <summary>Opens a one-off Build of Factor units of Good (steel needed for weapons).</summary>
    Requisition,
}

/// <summary>Applies at the start of Day, before that day's deliveries.</summary>
public sealed record Shock(int Day, ShockKind Kind)
{
    public string Source { get; init; } = "";

    /// <summary>The good a DestroyStock on world stock, StorageFull, SurgeDemand or Requisition names.</summary>
    public Good Good { get; init; }

    public double Factor { get; init; }

    public int Days { get; init; }

    internal static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    internal void Apply(RunState r)
    {
        var until = Day + Days;
        switch (Kind)
        {
            case ShockKind.DestroyStock when Source.Length == 0:
                r.Stock[Good] = r.Stock.GetValueOrDefault(Good) * (1 - Clamp01(Factor));
                return;
            case ShockKind.Threat:
                r.ThreatUntil = Math.Max(r.ThreatUntil, until);
                return;
            case ShockKind.PowerLoss:
                r.PowerFactor = Clamp01(Factor);
                r.PowerUntil = until;
                return;
            case ShockKind.StorageFull:
                r.CapUntil[Good] = until;
                r.CapLimit[Good] = Factor;
                return;
            case ShockKind.SurgeDemand:
                r.Surge[Good] = new Surge(Factor, until);
                return;
            case ShockKind.Requisition:
                r.Builds.Add(new BuildState(
                    new Build("requisition " + Good.Value, Day, [new Yield(Good, Factor)]),
                    new Dictionary<Good, double> { [Good] = Factor }));
                return;
            default:
                break;
        }
        var src = r.FindSource(Source);
        if (src is null)
        {
            return;
        }
        switch (Kind)
        {
            case ShockKind.ScaleCapacity:
                src.CapScale *= Factor;
                break;
            case ShockKind.DestroyStock:
                var f = 1 - Clamp01(Factor);
                src.Stock *= f;
                src.Planted *= f;
                break;
            case ShockKind.RemoveSource:
                src.Open = false;
                src.Removed = true;
                break;
            case ShockKind.PauseGrowth:
                src.PausedUntil = Math.Max(src.PausedUntil, until);
                break;
            case ShockKind.StopWork:
                src.OffUntil = Math.Max(src.OffUntil, until);
                break;
            default:
                break;
        }
    }
}

internal readonly record struct Surge(double Factor, int Until);

internal sealed class BuildState(Build build, Dictionary<Good, double> left)
{
    public Build Build { get; } = build;

    public Dictionary<Good, double> Left { get; } = left;

    public bool Done { get; set; }
}
