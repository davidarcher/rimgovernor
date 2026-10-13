using StoreDefenseLayoutRecord = RimGovernor.Brain.Store.DefenseLayoutRecord;

namespace RimGovernor.Brain.Snapshot;

/// <summary>
/// One native line-of-sight answer (observations_read_lines_of_fire) recorded beside the request it was read
/// against, from before the planner computed sight itself. Evidence only: the planner no longer reads or records
/// lines, and the policy's line of sight is checked against these.
/// </summary>
public sealed record NativeLine(Cell From, Cell To, Fact<bool> LineOfSight);

/// <summary>One decision of the defensive layout planner as recorded: the policy request it built from the native defense-site and combat-pawn reads, and the stored layout it decided against.</summary>
public sealed class SnapshotLayout
{
    public string Recorded { get; set; } = "";

    public GenerationSnapshot Snapshot { get; set; }

    public Tick Tick { get; set; }

    /// <summary>One of <see cref="SnapshotPackage.LayoutPropose"/>, <see cref="SnapshotPackage.LayoutTurrets"/>, <see cref="SnapshotPackage.LayoutCover"/>.</summary>
    public string Point { get; set; } = "";

    public DefenseRequest Request { get; set; } = new();

    public DefenseGeometry Geometry { get; set; } = new();

    public StoreDefenseLayoutRecord? Record { get; set; }

    /// <summary>The defense-site read's cells, which the cover selection consults beside the request (LayoutCover only).</summary>
    public IReadOnlyList<Bridge.Contracts.DefenseCell> Site { get; set; } = [];

    /// <summary>The native answers recorded before the line of sight replaced the read; only the oldest recordings carry them.</summary>
    public IReadOnlyList<NativeLine> NativeLines { get; set; } = [];
}

public static partial class SnapshotPackage
{
    /// <summary>The chokepoint choice: Request carries the lines of fire the proposal verified, replayed by DefenseLayouts.</summary>
    public const string LayoutPropose = "propose";

    /// <summary>The turret tier probe: Request and Geometry, replayed by DefenseTurrets.</summary>
    public const string LayoutTurrets = "turrets";

    /// <summary>The cover census: Request, Record and Site, replayed by DefenseApproachesFor over the record's layout.</summary>
    public const string LayoutCover = "cover";

    /// <summary>Writes l into dir as layout-&lt;point&gt;-&lt;tick&gt;.</summary>
    public static void RecordLayout(string dir, SnapshotLayout l) => Write(dir, "layout", $"layout-{l.Point}-{l.Tick.Value}", l);
}
