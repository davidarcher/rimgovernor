package policy

// LedgerMigratedOwner reports whether id's production bills are the ledger's
// to remove. Until the old bill machinery is deleted (the cleanup child of
// #2590) the ledger removes only a bill the journal says a migrated owner
// placed: one whose planner declares its orders (OrderDeclarer) and commits no
// bill method of its own, so an undeclared bill of its is a real orphan.
// MaintainWorkLedger is the owner of every bill the ledger placed; the owners
// listed beside it are the migrated planners, whose bills were placed under
// their own Standard before they migrated. A bill of any other owner, or of
// an owner the journal cannot name (after a reload), is kept whatever it
// matches. A planner joins this list in the change that registers its
// declarer.
func LedgerMigratedOwner(id ConcernID) bool {
	switch id {
	case MaintainWorkLedger, MaintainEquipment, MaintainResource, MaintainArt, MaintainPopulation, EnsureCooking, MaintainFoodStorage, EnsureFoodSupply, MaintainRefrigeration:
		return true
	}
	return false
}
