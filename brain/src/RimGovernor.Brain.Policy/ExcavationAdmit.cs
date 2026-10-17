namespace RimGovernor.Brain.Policy;

/// <summary>The native site-level counterfactual support answer. (Go <c>ExcavationSupport</c>.)</summary>
public enum ExcavationSupport : byte
{
    Unknown,
    Supported,
    Unsupported,
}

public static class ExcavationAdmitRules
{
    /// <summary>
    /// Go <c>ExcavationUnsupported</c>: native counterfactual roof support reports that removing this cell would
    /// leave roof outside the installed support radius of any remaining holder, or collapse is already pending.
    /// </summary>
    public static readonly Reason ExcavationUnsupported = new("excavation_unsupported");

    /// <summary>
    /// Go <c>ExcavationGeometryChanged</c>: the cell no longer holds the expected visible rock, holds a
    /// protected structure, or native mining refuses it.
    /// </summary>
    public static readonly Reason ExcavationGeometryChanged = new("excavation_geometry_changed");
}
