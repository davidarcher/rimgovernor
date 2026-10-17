namespace RimGovernor.Brain.Policy;

/// <summary>The native site-level counterfactual support answer.</summary>
public enum ExcavationSupport : byte
{
    Unknown,
    Supported,
    Unsupported,
}

public static class ExcavationAdmitRules
{
    /// <summary>
    /// Native counterfactual roof support reports that removing this cell would
    /// leave roof outside the installed support radius of any remaining holder, or collapse is already pending.
    /// </summary>
    public static readonly Reason ExcavationUnsupported = new("excavation_unsupported");

    /// <summary>
    /// The cell no longer holds the expected visible rock, holds a
    /// protected structure, or native mining refuses it.
    /// </summary>
    public static readonly Reason ExcavationGeometryChanged = new("excavation_geometry_changed");
}
