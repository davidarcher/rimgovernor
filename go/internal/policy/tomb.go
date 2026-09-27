package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Staging the tomb (#832). A dead colonist in a sarcophagus gives every
// colonist KnowBuriedInSarcophagus (+4 mood, 8 days, stacking), so once
// ComplexFurniture makes the sarcophagus available, a colonist corpse
// with no empty sarcophagus waiting raises the planned tomb's shell and
// then places the next free template sarcophagus. Vanilla haulers inter
// colonist corpses in any empty grave on their own; nothing here hauls.

// TombStepKind is the next tomb step.
type TombStepKind string

const (
	// TombNone: no unburied colonist corpse, enough empty sarcophagi, no
	// planned tomb, or a fact is unknown.
	TombNone TombStepKind = ""
	// TombShell: raise the walls and door of Room, the planned tomb.
	TombShell TombStepKind = "shell"
	// TombPlace: place Piece, the next free sarcophagus in Room.
	TombPlace TombStepKind = "place"
)

// TombStep is one bounded step towards a sarcophagus for every dead colonist.
type TombStep struct {
	Kind  TombStepKind
	Room  LayoutRoom
	Piece InteriorPiece
	// Dead is the unburied colonist corpses; Empty the empty sarcophagi.
	Dead, Empty int
}

// NextTombStep picks the next tomb step from the plan, the room census,
// the waste census and the colony's built buildings.
func NextTombStep(plan LayoutPlan, rooms RoomObservation, waste []WasteItem, built []CurrentBuilding) TombStep {
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
	if step.Dead == 0 {
		return TombStep{}
	}
	taken := map[domain.Cell]bool{}
	for _, b := range built {
		if b.Building.Definition() != SarcophagusDefinition {
			continue
		}
		if !filled[b.ID] {
			step.Empty++
		}
		for _, c := range b.Cells {
			taken[c] = true
		}
	}
	if step.Empty >= step.Dead {
		return TombStep{}
	}
	for _, r := range plan.Rooms {
		if r.Role != ModuleTomb || r.Dug {
			continue
		}
		step.Room = r
		if _, ok := PlannedRoomStanding(r, rooms); !ok {
			step.Kind = TombShell
			return step
		}
		in, ok := InteriorRoomFromLayout(r)
		if !ok {
			return TombStep{}
		}
		interior, ok := PlanInterior(in, InteriorPieceDefFor(SarcophagusDefinition))
		if !ok {
			return TombStep{}
		}
	pieces:
		for _, p := range interior.Pieces {
			for _, c := range rectCells(p.Rect) {
				if taken[c] {
					continue pieces
				}
			}
			step.Kind, step.Piece = TombPlace, p
			return step
		}
		return TombStep{}
	}
	return TombStep{}
}

// TombOwed is the review's tomb deficit: known true while a tomb step is
// due, false while the sarcophagus is unavailable, unknown while a fact
// the step reads is.
func TombOwed(available domain.Fact[bool], plan domain.Fact[LayoutPlan], rooms domain.Fact[RoomObservation], waste domain.Fact[[]WasteItem], built domain.Fact[CurrentConstruction]) domain.Fact[bool] {
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
	return domain.Known(NextTombStep(p, r, w, b.Buildings).Kind != TombNone)
}
