package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Staging the tomb (#832). A dead colonist in a sarcophagus gives every
// colonist KnowBuriedInSarcophagus (+4 mood, 8 days, stacking), so once
// ComplexFurniture makes the sarcophagus available, a colonist corpse
// with no empty grave waiting raises a planned tomb's shell and then
// places its next free template sarcophagus. The memory comes only from a
// sarcophagus's first burial, so a filled one is never reused: when every
// planned tomb is full the plan grows another (#857). Where no sarcophagus
// can be had (research, stuff, or no room for another tomb) a plain Grave
// takes the body instead. Vanilla haulers inter colonist corpses in any
// empty grave on their own; nothing here hauls.

// GraveDefinition is Core's plain grave: 1x2, no stuff, no research.
const GraveDefinition = "Grave"

// TombStepKind is the next tomb step.
type TombStepKind string

const (
	// TombNone: no unburied colonist corpse, enough empty graves, no
	// planned tomb, or a fact is unknown.
	TombNone TombStepKind = ""
	// TombShell: raise the walls and door of Room, the planned tomb.
	TombShell TombStepKind = "shell"
	// TombPlace: place Piece, the next free sarcophagus in Room.
	TombPlace TombStepKind = "place"
	// TombFull: every planned tomb's slots are taken; the plan owes
	// another tomb room, or a grave when none fits.
	TombFull TombStepKind = "full"
	// TombGrave: no sarcophagus can be had; place a plain grave.
	TombGrave TombStepKind = "grave"
)

// TombStep is one bounded step towards a grave for every dead colonist.
type TombStep struct {
	Kind  TombStepKind
	Room  LayoutRoom
	Piece InteriorPiece
	// Dead is the unburied colonist corpses; Empty the empty graves and
	// sarcophagi; Graves the plain graves standing.
	Dead, Empty, Graves int
}

// tombCensus counts the dead, the empty graves and the plain graves, and
// returns the cells graves stand on.
func tombCensus(waste []WasteItem, built []CurrentBuilding) (TombStep, map[domain.Cell]bool) {
	step := TombStep{}
	filled := map[string]bool{}
	for _, item := range waste {
		if item.CorpseOf != domain.CorpseColonist {
			continue
		}
		if item.State == WasteBuried {
			filled[item.Grave] = true
		} else {
			step.Dead++
		}
	}
	taken := map[domain.Cell]bool{}
	for _, b := range built {
		def := b.Building.Definition()
		if def != SarcophagusDefinition && def != GraveDefinition {
			continue
		}
		if def == GraveDefinition {
			step.Graves++
		}
		if !filled[b.ID] {
			step.Empty++
		}
		for _, c := range b.Cells {
			taken[c] = true
		}
	}
	return step, taken
}

// tombSlot is the room's first template sarcophagus slot no grave stands on.
func tombSlot(r LayoutRoom, shapes PieceShapes, taken map[domain.Cell]bool) (InteriorPiece, bool) {
	in, ok := InteriorRoomFromLayout(r, shapes)
	if !ok {
		return InteriorPiece{}, false
	}
	sarcophagus, ok := in.Piece(SarcophagusDefinition)
	if !ok {
		return InteriorPiece{}, false
	}
	interior, ok := PlanInterior(in, sarcophagus)
	if !ok {
		return InteriorPiece{}, false
	}
pieces:
	for _, p := range interior.Pieces {
		for _, c := range rectCells(p.Rect) {
			if taken[c] {
				continue pieces
			}
		}
		return p, true
	}
	return InteriorPiece{}, false
}

// NextTombStep picks the next tomb step from the plan, the room census,
// the waste census and the colony's built buildings. sarcophagus is false
// when none can be had (unresearched, no stuff), which leaves a grave.
func NextTombStep(plan LayoutPlan, rooms RoomObservation, waste []WasteItem, built []CurrentBuilding, sarcophagus bool) TombStep {
	step, taken := tombCensus(waste, built)
	if step.Dead == 0 || step.Empty >= step.Dead {
		return TombStep{}
	}
	if !sarcophagus {
		step.Kind = TombGrave
		return step
	}
	for _, r := range plan.AllRooms() {
		if r.Role != ModuleTomb {
			continue
		}
		piece, ok := tombSlot(r, rooms.Shapes, taken)
		if !ok {
			continue
		}
		step.Room = r
		if _, ok := PlannedRoomStanding(r, rooms); !ok {
			step.Kind = TombShell
			return step
		}
		step.Kind, step.Piece = TombPlace, piece
		return step
	}
	step.Kind = TombFull
	return step
}

// TombRooms is the plan's tomb rooms that a sarcophagus can stand in.
func (p LayoutPlan) TombRooms() int {
	n := 0
	for _, r := range p.AllRooms() {
		if r.Role == ModuleTomb {
			n++
		}
	}
	return n
}

// TombOwed is the review's tomb deficit: known true while a tomb step is
// due, unknown while a fact the step reads is.
func TombOwed(available domain.Fact[bool], plan domain.Fact[LayoutPlan], rooms domain.Fact[RoomObservation], waste domain.Fact[[]WasteItem], built domain.Fact[CurrentConstruction]) domain.Fact[bool] {
	a, ak := available.Value()
	p, pk := plan.Value()
	r, rk := rooms.Value()
	w, wk := waste.Value()
	b, bk := built.Value()
	if !wk || !bk || !b.Colony {
		return domain.Unknown[bool]()
	}
	if ak && !a {
		return domain.Known(NextTombStep(LayoutPlan{}, RoomObservation{}, w, b.Buildings, false).Kind != TombNone)
	}
	if !ak || !pk || !rk {
		return domain.Unknown[bool]()
	}
	return domain.Known(NextTombStep(p, r, w, b.Buildings, true).Kind != TombNone)
}
