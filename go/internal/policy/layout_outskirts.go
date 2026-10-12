package policy

// The outskirts: one off-core cluster per colony holding
// the morgue, tomb, graveyard, waste yard and incinerator. It is a reservation
// set back outskirtsGap cells from every room (the dump clearance and the
// corpse-sight radius). The tiler sites it; the old generator's side-by-side
// siting is gone.

// ReserveOutskirts is the outskirts cluster's outline: the region its rooms
// are sited in, reserved so the packer and later sites leave it whole.
const ReserveOutskirts ReservationKind = "outskirts"

const (
	// CorpseSightRadius is how far a pawn sees a corpse, in cells: PawnObserver
	// (1.6, ilspycmd) takes a corpse for observed while it lies within
	// InHorDistOf(pawn, ObservationRadius = 5) and in line of sight, and gives
	// the observer ObservedLayingCorpse / ObservedLayingRottingCorpse.
	CorpseSightRadius int32 = 5
)

// OutskirtsArea is the plan's outskirts reservation, false when it holds none.
func (p LayoutPlan) OutskirtsArea() (Region, bool) {
	for _, r := range p.Reservations {
		if r.Kind == ReserveOutskirts {
			return r.Area, true
		}
	}
	return Region{}, false
}
