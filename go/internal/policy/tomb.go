package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Staging the tomb (#832, #2196). A dead colonist in a sarcophagus gives every
// colonist KnowBuriedInSarcophagus (+4 mood, 8 days, stacking), so once
// ComplexFurniture makes the sarcophagus available, a colonist corpse
// with no empty grave waiting raises a planned tomb's shell and then
// reconciles its room (ReconcileRoom) to the next free template sarcophagus:
// the ring, the floor and the piece, installed from packed stock first. The memory comes only from a
// sarcophagus's first burial, so a filled one is never reused: when every
// planned tomb is full the plan grows another (#857). Where no sarcophagus
// can be had (research or stuff) a plain Grave takes the body instead, placed
// only in the next free slot of a planned graveyard (GraveyardSlots); the
// graveyard's fence and gate are raised with the grave. When the graveyard
// has no slot left the body waits in the morgue and the department asks layout
// for a further graveyard (GraveyardsWanted). Vanilla haulers inter colonist
// corpses in any empty grave on their own; nothing here hauls.

// GraveDefinition is Core's plain grave: 1x2, no stuff, no research.
const GraveDefinition = "Grave"

// TombStepKind is the next tomb step.
type TombStepKind string

const (
	// TombNone: no unburied colonist corpse, enough empty graves, no
	// planned tomb, or a fact is unknown.
	TombNone TombStepKind = ""
	// TombReconcile: reconcile Room, the planned tomb, to Template, its next
	// free sarcophagus, or Room, the planned graveyard, to Template, its next
	// free grave (ReconcileRoom): the ring, the floor and the piece,
	// whatever the diff still owes. There is no shell or place step.
	TombReconcile TombStepKind = "reconcile"
	// TombFull: every planned tomb's slots are taken; the plan owes
	// another tomb room.
	TombFull TombStepKind = "full"
)

// TombStep is one bounded step towards a grave for every dead colonist.
type TombStep struct {
	Kind TombStepKind
	Room PlannedRoom
	// Template is a TombReconcile's wanted furniture: the next free
	// sarcophagus or grave in its slot.
	Template []WantedPiece
	// Dead is the unburied colonist corpses; Empty the empty graves and
	// sarcophagi; Graves the plain graves standing.
	Dead, Empty, Graves int
}

// tombCensus counts the dead, the empty graves and the plain graves, and
// returns the cells graves stand on.
func tombCensus(waste []WasteItem, built []CurrentBuilding, sarcophagus string) (TombStep, map[domain.Cell]bool) {
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
		if def != sarcophagus && def != GraveDefinition {
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
func tombSlot(r PlannedRoom, shapes PieceShapes, taken map[domain.Cell]bool) (InteriorPiece, bool) {
	in, ok := InteriorRoomFromLayout(r, shapes)
	if !ok {
		return InteriorPiece{}, false
	}
	sarcophagus, ok := in.Piece(shapes.Furniture.Sarcophagus)
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

// NextTombStep picks the next tomb step from the plan, the waste census and
// the colony's built buildings. sarcophagus is false when none can be had
// (unresearched, no stuff), which leaves a grave in the planned graveyard; with
// no free grave slot there is no step, the body waits and the graveyard is
// asked for again (GraveyardsWanted).
func NextTombStep(plan LayoutPlan, waste []WasteItem, built []CurrentBuilding, shapes PieceShapes, sarcophagus bool) TombStep {
	step, taken := tombCensus(waste, built, shapes.Furniture.Sarcophagus)
	if step.Dead == 0 || step.Empty >= step.Dead {
		return TombStep{}
	}
	if !sarcophagus {
		for _, r := range plan.roomsOf(PlannedGraveyard) {
			if piece, ok := graveSlot(r, taken); ok {
				step.Room, step.Kind, step.Template = r, TombReconcile, []WantedPiece{piece.Wanted()}
				return step
			}
		}
		return TombStep{}
	}
	for _, r := range plan.AllRooms() {
		if r.Role != PlannedTomb {
			continue
		}
		piece, ok := tombSlot(r, shapes, taken)
		if !ok {
			continue
		}
		step.Room = r
		step.Kind, step.Template = TombReconcile, []WantedPiece{piece.Wanted()}
		return step
	}
	step.Kind = TombFull
	return step
}

// TombRooms is the plan's tomb rooms that a sarcophagus can stand in.
func (p LayoutPlan) TombRooms() int {
	n := 0
	for _, r := range p.AllRooms() {
		if r.Role == PlannedTomb {
			n++
		}
	}
	return n
}

// TombOwed is the review's tomb deficit: known true while a tomb step is
// due, unknown while a fact the step reads is.
func TombOwed(shapes PieceShapes, available domain.Fact[bool], plan domain.Fact[LayoutPlan], rooms domain.Fact[RoomObservation], waste domain.Fact[[]WasteItem], built domain.Fact[CurrentConstruction]) domain.Fact[bool] {
	a, ak := available.Value()
	p, pk := plan.Value()
	_, rk := rooms.Value()
	w, wk := waste.Value()
	b, bk := built.Value()
	if !wk || !bk || !b.Colony {
		return domain.Unknown[bool]()
	}
	if !pk {
		return domain.Unknown[bool]()
	}
	if ak && !a {
		return domain.Known(NextTombStep(p, w, b.Buildings, shapes, false).Kind != TombNone)
	}
	if !ak || !rk {
		return domain.Unknown[bool]()
	}
	return domain.Known(NextTombStep(p, w, b.Buildings, shapes, true).Kind != TombNone)
}
