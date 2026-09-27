package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Cremating raiders (#833). A stranger's corpse lying unburied is a mood
// hit and a rot source; once the crematorium is available, MaintainWaste
// places one in a free workshop slot and gives it a forever CremateCorpse
// bill that takes stranger corpses only. Colonists go to the tomb (#832)
// and are never cremated; animals stay with the butcher.

// CrematoriumDefinition is the crematorium the waste routine places.
const CrematoriumDefinition = "ElectricCrematorium"

// CremationStepKind is the next cremation step.
type CremationStepKind string

const (
	// CremationNone: no unburied stranger corpse, no free workshop slot,
	// or a fact is unknown.
	CremationNone CremationStepKind = ""
	// CremationPlace: place Piece, a free workshop slot, as the crematorium.
	CremationPlace CremationStepKind = "place"
	// CremationBill: Bench is a built crematorium; it needs its bill.
	CremationBill CremationStepKind = "bill"
)

// CremationStep is one bounded step towards cremating stranger corpses.
type CremationStep struct {
	Kind      CremationStepKind
	Room      LayoutRoom
	Piece     InteriorPiece
	Bench     string
	Strangers int
}

// NextCremationStep picks the next cremation step from the plan, the room
// census, the waste census and the colony's built buildings. The bill
// step does not know whether the bench already carries the bill; the
// caller reads the bench's bills.
func NextCremationStep(plan LayoutPlan, rooms RoomObservation, waste []WasteItem, built []CurrentBuilding) CremationStep {
	step := CremationStep{}
	for _, item := range waste {
		if item.CorpseOf == domain.CorpseStranger && item.State != WasteBuried {
			step.Strangers++
		}
	}
	if step.Strangers == 0 {
		return CremationStep{}
	}
	taken := map[domain.Cell]bool{}
	for _, b := range built {
		if b.Building.Definition() == CrematoriumDefinition && (step.Bench == "" || b.ID < step.Bench) {
			step.Kind, step.Bench = CremationBill, b.ID
		}
		for _, c := range b.Cells {
			taken[c] = true
		}
	}
	if step.Kind == CremationBill {
		return step
	}
	for _, r := range plan.Rooms {
		if r.Role != ModuleWorkshop {
			continue
		}
		if _, ok := PlannedRoomStanding(r, rooms); !ok {
			continue
		}
		in, ok := InteriorRoomFromLayout(r)
		if !ok {
			continue
		}
		interior, ok := PlanInterior(in, InteriorPieceDefFor(CrematoriumDefinition))
		if !ok {
			continue
		}
	pieces:
		for _, p := range interior.Pieces {
			if !p.Accepts(CrematoriumDefinition) {
				continue
			}
			for _, c := range rectCells(p.Rect) {
				if taken[c] {
					continue pieces
				}
			}
			step.Kind, step.Room, step.Piece = CremationPlace, r, p
			return step
		}
	}
	return CremationStep{}
}

// CremationOwed is the review's cremation deficit: known true while a
// cremation step is due, false while the crematorium is unavailable,
// unknown while a fact the step reads is. A built crematorium keeps it
// owed until the stranger corpses are gone: the bill step reads the
// bench's bills and falls through to the haul once the bill stands.
func CremationOwed(available domain.Fact[bool], plan domain.Fact[LayoutPlan], rooms domain.Fact[RoomObservation], waste domain.Fact[[]WasteItem], built domain.Fact[CurrentConstruction]) domain.Fact[bool] {
	a, ak := available.Value()
	if ak && !a {
		return domain.Known(false)
	}
	p, pk := plan.Value()
	r, rk := rooms.Value()
	w, wk := waste.Value()
	b, bk := built.Value()
	if !ak || !pk || !rk || !wk || !bk || !b.Colony {
		return domain.Unknown[bool]()
	}
	return domain.Known(NextCremationStep(p, r, w, b.Buildings).Kind != CremationNone)
}
