namespace RimGovernor.Brain.Policy;

// Go: bill_gap.go

/// <summary>
/// Why a bill selector chose no bill: a closed set the planner turns into the verdict it reports. The
/// <see cref="None"/> value means a bill was chosen.
/// </summary>
public enum BillGap
{
    None,

    /// <summary>Nothing is owed, so no bill is wanted.</summary>
    [WireName("nothing_wanted")]
    NothingWanted,

    /// <summary>A bill for the want already stands.</summary>
    [WireName("in_production")]
    InProduction,

    /// <summary>Something is owed, but no usable bench offers a researched recipe for it.</summary>
    [WireName("no_usable_recipe")]
    NoRecipe,

    /// <summary>A usable bench offers the recipe but every such bench holds a full bill list.</summary>
    [WireName("bench_bills_full")]
    BenchFull,

    /// <summary>Wastepacks lie uncleared (or unread), so no gestation.</summary>
    [WireName("uncleared_waste")]
    Waste,

    /// <summary>No mech charger is ready, so no gestation.</summary>
    [WireName("no_ready_charger")]
    NoCharger,
}
