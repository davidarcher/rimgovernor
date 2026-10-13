using RimGovernor.Brain.Policy;
using RimGovernor.Protocol.Common;
using DomainCell = RimGovernor.Brain.Domain.Cell;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's <c>ExcavationSiteCell</c>: one requested rock cell as native saw it. Fogged cells carry no rock, roof or
/// eligibility facts: they are unknown, never assumed empty or safe.
/// </summary>
public sealed record ExcavationSiteCell
{
    public DomainCell Cell { get; init; }

    public bool Fogged { get; init; }

    public string Definition { get; init; } = "";

    public bool Walkable { get; init; }

    public bool MineDesignated { get; init; }

    public bool Eligible { get; init; }

    public string Blocker { get; init; } = "";
}

/// <summary>
/// Go's <c>ExcavationSite</c>: the site-level answer for one ordered cell set, the counterfactual roof support once
/// every requested cell is gone, plus worker access to the access cell. The read (<c>ReadExcavationSite</c>) is
/// wire plumbing and not ported.
/// </summary>
public sealed record ExcavationSite
{
    public ObservationContext? Context { get; init; }

    public IReadOnlyList<ExcavationSiteCell> Cells { get; init; } = [];

    public ExcavationSupport Support { get; init; }

    public uint RoofCellsChecked { get; init; }

    public string SupportBlocker { get; init; } = "";

    public bool CollapsePending { get; init; }

    public bool WorkerAvailable { get; init; }

    public IReadOnlyList<string> Workers { get; init; } = [];

    public bool AccessReachable { get; init; }
}

public static partial class BridgeFunctions
{
    /// <summary>Go's <c>excavationCellValid</c>: whether the cell lies on the map grid.</summary>
    public static bool ExcavationCellValid(DomainCell cell) => cell.X >= 0 && cell.Z >= 0;
}
