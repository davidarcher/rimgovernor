using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Executor;

/// <summary>One decision of the defensive layout planner as recorded: the policy request it built from the native defense-site and combat-pawn reads, and the stored layout it decided against.</summary>
public sealed class SnapshotLayout
{
    public string Recorded { get; set; } = "";

    public GenerationSnapshot Snapshot { get; set; }

    public Tick Tick { get; set; }

    /// <summary>One of <see cref="SnapshotLayoutReplay.LayoutPropose"/>, <see cref="SnapshotLayoutReplay.LayoutTurrets"/>, <see cref="SnapshotLayoutReplay.LayoutCover"/>.</summary>
    public string Point { get; set; } = "";

    public DefenseRequest Request { get; set; } = new();

    public DefenseGeometry Geometry { get; set; } = new();

    /// <summary>The stored layout the cover census replays over: the record's tiers with the plan's firing line and trap lane (LayoutCover only).</summary>
    public DefenseLayout? Layout { get; set; }

    /// <summary>The defense-site read's cells, which the cover selection consults beside the request (LayoutCover only).</summary>
    public IReadOnlyList<O.DefenseCell> Site { get; set; } = [];
}

public static class SnapshotLayoutReplay
{
    /// <summary>The chokepoint choice: Request carries the lines of fire the proposal verified, replayed by DefenseLayouts.</summary>
    public const string LayoutPropose = "propose";

    /// <summary>The turret tier probe: Request and Geometry, replayed by DefenseTurrets.</summary>
    public const string LayoutTurrets = "turrets";

    /// <summary>The cover census: Request, Layout and Site, replayed by DefenseApproachesFor over the layout.</summary>
    public const string LayoutCover = "cover";

    /// <summary>Writes l into dir as layout-&lt;point&gt;-&lt;tick&gt;.</summary>
    public static void RecordLayout(string dir, SnapshotLayout l) => SnapshotRecorder.Write(dir, "layout", $"layout-{l.Point}-{l.Tick.Value}", l);
}
