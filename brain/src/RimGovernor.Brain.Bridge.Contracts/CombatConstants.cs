namespace RimGovernor.Brain.Bridge.Contracts;

// Go combat_orders.go, combat_geometry.go and placement.go consts.
public static partial class BridgeConstants
{
    /// <summary>Go's <c>PlacementBatchLimit</c>: the most placements one placement_preview request may carry.</summary>
    public const int PlacementBatchLimit = 64;

    /// <summary>Go's <c>CombatGeometryMaxCells</c>.</summary>
    public const int CombatGeometryMaxCells = 64;

    /// <summary>Go's <c>CombatGeometryMaxHostiles</c>.</summary>
    public const int CombatGeometryMaxHostiles = 16;

    /// <summary>Go's <c>CombatGeometryMaxRadius</c>: bounds a firing_cells proposal's search.</summary>
    public const int CombatGeometryMaxRadius = 12;

    /// <summary>A pawn order for a pawn that is not drafted.</summary>
    public const string CombatRefusalNotDrafted = "not_drafted";

    public const string CombatRefusalStaleSnapshot = "stale_snapshot";

    public const string CombatRefusalNotFound = "not_found";

    public const string CombatRefusalUnreachable = "unreachable";

    public const string CombatRefusalCannotHit = "cannot_hit";

    public const string CombatRefusalNoGroundVerb = "no_ground_verb";

    public const string CombatRefusalNotADoor = "not_a_door";

    public const string CombatRefusalNativeRefused = "native_refused";

    /// <summary>Rescue eligibility or bed search failed.</summary>
    public const string CombatRefusalCannotRescue = "cannot_rescue";

    public const string CombatRefusalNoBed = "no_bed";

    /// <summary>No damaged player building on the cell, or the pawn cannot construct.</summary>
    public const string CombatRefusalCannotRepair = "cannot_repair";

    /// <summary>The pawn is not eligible to draft.</summary>
    public const string CombatRefusalCannotDraft = "cannot_draft";

    /// <summary>No unroofed player mortar on the cell.</summary>
    public const string CombatRefusalNotAMortar = "not_a_mortar";

    /// <summary>The mortar does not accept the shell.</summary>
    public const string CombatRefusalUnknownShell = "unknown_shell";

    /// <summary>No unforbidden stack of the shell is in reach.</summary>
    public const string CombatRefusalNoShell = "no_shell";

    /// <summary>Not a spawned player animal.</summary>
    public const string CombatRefusalNotOurs = "not_ours";

    /// <summary>A release order to an animal without the Release training.</summary>
    public const string CombatRefusalUntrained = "untrained";

    /// <summary>The def is not a drug.</summary>
    public const string CombatRefusalNotADrug = "not_a_drug";

    /// <summary>None is carried; legality (age, high, addiction) is the game's: native_refused.</summary>
    public const string CombatRefusalNoDrug = "no_drug";
}
