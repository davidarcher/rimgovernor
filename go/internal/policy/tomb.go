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
//
// Stranger corpses (raiders, visitors, prisoners; #2336) feed the tomb too, for
// the mood memory alone: a stranger is owed a fresh sarcophagus only while
// fewer than StrangerTombStackCap KnowBuriedInSarcophagus stacks are live across
// the colonists and the next sarcophagus is funded from stock (StrangerTomb).
// Over the cap, unfunded or with the mood thoughts unread, a stranger keeps the
// morgue path; the plain grave never takes one.
//
// A stranger's burial in a fresh sarcophagus is its first body ever, so the
// memory has fired (#2337): while the memory is live the filled sarcophagus is
// deconstructed (TombDispose, StrangerDisposals). Native ejects the corpse next
// to the cell, where the incinerator's burnable filter takes it, and the cell is
// a free tomb slot again. Only a sarcophagus in a planned tomb that holds
// nothing but strangers is touched; a colonist's sarcophagus and every grave
// stay.

// KnowBuriedInSarcophagusThought is the memory a colonist gains when the first
// body ever is hauled into a sarcophagus; it stacks (4, 2, 1, 0.5) for 8 days.
const KnowBuriedInSarcophagusThought = "KnowBuriedInSarcophagus"

// StrangerTombStackCap is the most live KnowBuriedInSarcophagus stacks the
// tomb feeds strangers for.
const StrangerTombStackCap = 4

// knowBuriedStackTotals is a colonist's summed memory offset at 1, 2, 3 and 4
// live stacks.
var knowBuriedStackTotals = [StrangerTombStackCap]float64{4, 6, 7, 7.5}

// KnowBuriedStacks is the live KnowBuriedInSarcophagus stacks across the
// colonists: the most any colonist carries, read from the summed memory offset.
// Unknown while any colonist's thoughts, or the census, are unread.
func KnowBuriedStacks(pawns domain.Fact[[]MoodPawn]) (int, bool) {
	rows, known := pawns.Value()
	if !known {
		return 0, false
	}
	live := 0
	for _, p := range rows {
		thoughts, ok := p.Thoughts.Value()
		if !ok {
			return 0, false
		}
		for _, t := range thoughts {
			if t.Def != KnowBuriedInSarcophagusThought {
				continue
			}
			n := 0
			for _, total := range knowBuriedStackTotals {
				if t.Offset >= total-0.01 {
					n++
				}
			}
			live = max(live, n)
		}
	}
	return live, true
}

// StrangerTomb is what the tomb reads to count stranger corpses: the live
// memory stacks and whether the next sarcophagus is funded from stock. The zero
// value (unread thoughts, or unfunded) stages no stranger.
type StrangerTomb struct {
	Live   int
	Funded bool
}

// Allowed is how many stranger corpses are owed a sarcophagus.
func (s StrangerTomb) Allowed() int {
	if !s.Funded {
		return 0
	}
	return max(StrangerTombStackCap-s.Live, 0)
}

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
	// TombDispose: Disposal are filled stranger sarcophagi whose memory has
	// fired; the planner deconstructs one.
	TombDispose TombStepKind = "dispose"
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
	// Disposal is a TombDispose's filled stranger sarcophagi, in census order.
	Disposal []CurrentBuilding
}

// StrangerDisposals is the standing sarcophagi to deconstruct (#2337): in a
// planned tomb, holding a buried stranger and no colonist, while the memory is
// live (strangers.Live > 0; unread thoughts leave it zero). Graves and any
// sarcophagus outside the plan are never listed.
func StrangerDisposals(plan LayoutPlan, waste []WasteItem, built []CurrentBuilding, sarcophagus string, strangers StrangerTomb) []CurrentBuilding {
	if strangers.Live <= 0 {
		return nil
	}
	tomb := map[domain.Cell]bool{}
	for _, r := range plan.roomsOf(PlannedTomb) {
		for _, c := range rectCells(r.Interior) {
			tomb[c] = true
		}
	}
	// holds[grave] is true while every buried corpse in it is a stranger.
	holds := map[string]bool{}
	for _, item := range waste {
		if item.State != WasteBuried || item.Grave == "" {
			continue
		}
		stranger := item.CorpseOf == domain.CorpseStranger
		if prior, seen := holds[item.Grave]; !seen {
			holds[item.Grave] = stranger
		} else {
			holds[item.Grave] = prior && stranger
		}
	}
	var out []CurrentBuilding
	for _, b := range built {
		if b.Building.Definition() != sarcophagus || !holds[b.ID] || len(b.Cells) == 0 {
			continue
		}
		inside := true
		for _, c := range b.Cells {
			inside = inside && tomb[c]
		}
		if inside {
			out = append(out, b)
		}
	}
	return out
}

// tombCensus counts the dead, the empty graves and the plain graves, and
// returns the cells graves stand on. Strangers count as dead only up to
// strangers (StrangerTomb.Allowed); a buried body of either kind fills its grave.
func tombCensus(waste []WasteItem, built []CurrentBuilding, sarcophagus string, strangers int) (TombStep, map[domain.Cell]bool) {
	step := TombStep{}
	filled := map[string]bool{}
	stranded := 0
	for _, item := range waste {
		if item.CorpseOf != domain.CorpseColonist && item.CorpseOf != domain.CorpseStranger {
			continue
		}
		switch {
		case item.State == WasteBuried:
			filled[item.Grave] = true
		case item.CorpseOf == domain.CorpseColonist:
			step.Dead++
		default:
			stranded++
		}
	}
	step.Dead += min(stranded, strangers)
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
func NextTombStep(plan LayoutPlan, waste []WasteItem, built []CurrentBuilding, shapes PieceShapes, sarcophagus bool, strangers StrangerTomb) TombStep {
	if dispose := StrangerDisposals(plan, waste, built, shapes.Furniture.Sarcophagus, strangers); len(dispose) > 0 {
		return TombStep{Kind: TombDispose, Disposal: dispose}
	}
	allowed := 0
	if sarcophagus {
		allowed = strangers.Allowed()
	}
	step, taken := tombCensus(waste, built, shapes.Furniture.Sarcophagus, allowed)
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
func TombOwed(shapes PieceShapes, available domain.Fact[bool], plan domain.Fact[LayoutPlan], rooms domain.Fact[RoomObservation], waste domain.Fact[[]WasteItem], built domain.Fact[CurrentConstruction], strangers StrangerTomb) domain.Fact[bool] {
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
		return domain.Known(NextTombStep(p, w, b.Buildings, shapes, false, strangers).Kind != TombNone)
	}
	if !ak || !rk {
		return domain.Unknown[bool]()
	}
	return domain.Known(NextTombStep(p, w, b.Buildings, shapes, true, strangers).Kind != TombNone)
}
