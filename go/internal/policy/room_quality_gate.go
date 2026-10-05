package policy

// RoomGate is the personal-share gate on in-place bedroom upgrades (#1840).
// RoomTarget.Min is the tier ceiling a room climbs to; the gate decides
// whether an owner's remaining share (#1836) pays for the next step. The
// zero value (no Shares) is ungated, so callers that carry no share keep
// working.
//
// A step is charged its market-value delta. A delta of zero or less (a
// necessity), and any step in a room with no owners (a common or throne room,
// which stay on the baseline), is never charged. A charged step needs the
// Reserves stage (a Foothold colony spends nothing on comfort) and the
// owners' combined remaining share: a shared room spends its owners' remaining
// shares together, and an owner with an unknown remaining refuses it.
type RoomGate struct {
	Stage ColonyStage
	// Shares is the colonist's share, nil for an ungated gate
	// (observation.ColonyProjection.PersonalShareOf).
	Shares func(PawnID) PersonalShare
	// Items prices a floor's materials; BedPrice prices a stuffed bed;
	// PiecePrice prices a template piece in the stuff it would be built in
	// (ItemFacts carries no row for a stuff-made def).
	Items      ItemFacts
	BedPrice   BedPrice
	PiecePrice func(def string) (float64, bool)
	// SuiteBed and SuiteBedStuff are the bed a new suite is furnished with and
	// SuitePieces the template pieces it takes (#1841); a gated gate with no
	// SuiteBed prices no suite and refuses its claims.
	SuiteBed      Resource
	SuiteBedStuff Resource
	SuitePieces   []string
}

// furnishPrice is what furnishing a suite costs its claimant: the new bed's
// delta over the bed they own plus the template pieces (walls, doors and
// floor are not charged), false when any part is unpriced.
func (g RoomGate) furnishPrice(owned SleepingBed) (float64, bool) {
	total, ok := g.bedDelta(g.SuiteBed, g.SuiteBedStuff, owned)
	if !ok || g.SuiteBed == "" {
		return 0, false
	}
	for _, def := range g.SuitePieces {
		if g.PiecePrice == nil {
			return 0, false
		}
		price, ok := g.PiecePrice(def)
		if !ok {
			return 0, false
		}
		total += price
	}
	return total, true
}

// gateSuiteClaims drops the claims whose owner's remaining share does not pay
// the furnishing left to do (#1841), keeping the order. The claims take the
// vacant suites in order, so a claim is priced against the suite it would
// take: one with a bed standing has only the move left (free), any other,
// including a suite still to be built, is charged its furnishing. A gated
// claim whose price is unknown is dropped, so MaintainHousing can recover.
func gateSuiteClaims(claims []SuiteClaim, plan LayoutPlan, rooms RoomObservation, sleeping SleepingObservation, gate RoomGate) []SuiteClaim {
	if gate.Shares == nil {
		return claims
	}
	beds := map[string]SleepingBed{}
	for _, b := range sleeping.Beds {
		beds[b.ID] = b
	}
	vacant := vacantSuites(plan, rooms, sleeping)
	var out []SuiteClaim
	for _, c := range claims {
		if i := len(out); i < len(vacant) {
			if room, ok := CensusRoomIn(vacant[i], rooms); ok && len(room.Beds) > 0 {
				out = append(out, c)
				continue
			}
		}
		owned, known := beds[c.Bed]
		price, priced := gate.furnishPrice(owned)
		if known && priced && gate.Allows([]PawnID{c.Pawn}, price) {
			out = append(out, c)
		}
	}
	return out
}

// Allows is whether the owners may spend delta now.
func (g RoomGate) Allows(owners []PawnID, delta float64) bool {
	if g.Shares == nil || len(owners) == 0 || delta <= 0 {
		return true
	}
	if g.Stage < StageReserves {
		return false
	}
	total := 0.0
	for _, o := range owners {
		s := g.Shares(o)
		if !s.Gated {
			return true
		}
		remaining, ok := s.Remaining.Value()
		if !ok {
			return false
		}
		total += remaining
	}
	return delta <= total
}

// PieceAllowed is whether the owners may place one def: its market value is
// the delta. A def the catalog cannot price is refused while gated.
func (g RoomGate) PieceAllowed(owners []PawnID, def string) bool {
	if g.Shares == nil || len(owners) == 0 {
		return true
	}
	if g.PiecePrice == nil {
		return false
	}
	price, ok := g.PiecePrice(def)
	return ok && g.Allows(owners, price)
}

// FloorCellPrice is the material price of one cell of floor d, false when the
// costs or a material's market value are unread.
func (g RoomGate) FloorCellPrice(d FloorDefinition) (float64, bool) {
	costs, ok := d.Costs.Value()
	if !ok {
		return 0, false
	}
	total := 0.0
	for _, c := range costs {
		price, err := g.Items.MarketValue(c.Resource)
		if err != nil {
			return 0, false
		}
		total += price * float64(c.Count)
	}
	return total, true
}

// bedAllowed is whether the owners may rebuild owned as def in stuff: the
// price delta must be known and fit.
func (g RoomGate) bedAllowed(owners []PawnID, def, stuff Resource, owned SleepingBed) bool {
	if g.Shares == nil {
		return true
	}
	delta, ok := g.bedDelta(def, stuff, owned)
	return ok && g.Allows(owners, delta)
}

// bedDelta is the market-value delta of a new bed of def in stuff (Normal
// quality; empty stuff reads as the owned bed's) over the owned bed, false
// when either is unpriced.
func (g RoomGate) bedDelta(def, stuff Resource, owned SleepingBed) (float64, bool) {
	if g.BedPrice == nil {
		return 0, false
	}
	ownedStuff, _ := owned.Stuff.Value()
	if stuff == "" {
		stuff = Resource(ownedStuff)
	}
	quality, _ := owned.Quality.Value()
	q, known := qualityByName(quality)
	base, ok := g.BedPrice(def, stuff)
	ownedBase, ownedOk := g.BedPrice(owned.Definition, Resource(ownedStuff))
	if !known || !ok || !ownedOk {
		return 0, false
	}
	next, nk := ItemMarketValue(base, 2, 1).Value()
	have, hk := ItemMarketValue(ownedBase, q, 1).Value()
	return next - have, nk && hk
}
