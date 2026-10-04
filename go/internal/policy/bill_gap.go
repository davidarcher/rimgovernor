package policy

// BillGap is why a bill selector chose no bill: a closed set the planner
// turns into the verdict it reports. The zero value means a bill was chosen.
type BillGap string

// Bill gaps.
const (
	// BillGapNothingWanted: nothing is owed, so no bill is wanted.
	BillGapNothingWanted BillGap = "nothing_wanted"
	// BillGapInProduction: a bill for the want already stands.
	BillGapInProduction BillGap = "in_production"
	// BillGapNoRecipe: something is owed, but no usable bench offers a
	// researched recipe for it.
	BillGapNoRecipe BillGap = "no_usable_recipe"
	// BillGapBenchFull: a usable bench offers the recipe but every such bench
	// holds a full bill list.
	BillGapBenchFull BillGap = "bench_bills_full"
	// BillGapWaste: wastepacks lie uncleared (or unread), so no gestation.
	BillGapWaste BillGap = "uncleared_waste"
	// BillGapNoCharger: no mech charger is ready, so no gestation.
	BillGapNoCharger BillGap = "no_ready_charger"
)
