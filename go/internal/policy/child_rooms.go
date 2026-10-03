package policy

import (
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Staging the Biotech child rooms (#1680, epic #1667): the nursery,
// playroom and classroom. A room role is the game's own score over the
// room's contents (Rooms wiki, Roles): a Nursery needs at least two baby
// beds (a crib or a baby sleeping spot) and no other bed, a Playroom scores
// each toy box and baby decoration and a Classroom each blackboard and
// school desk, both only while the room holds no humanlike bed. The
// planner owes a role's room while a pawn of the matching developmental
// stage lives in the colony: the plan grows a room sized to the furniture
// (GrowChildRoom), the sleeping planner raises its shell and places the
// furniture at the template's slots (NextChildRoomStep). Footprints are the
// native definition catalog's, never constants here. Using the rooms
// (feeding, play, lessons) is the next children's.

// Child room furniture, by the game's definition names. A role's row in
// FacilityCatalog lists them; the room scores any one of its role's defs.
var (
	NurseryBedDefinitions   = []string{"Crib", "BabySleepingSpot"}
	PlayroomToyDefinitions  = []string{"ToyBox"}
	PlayroomDecoDefinitions = []string{"BabyDecoration"}
	ClassroomBoardDefs      = []string{"Blackboard"}
	ClassroomDeskDefs       = []string{"SchoolDesk"}
)

// ModuleNursery, ModulePlayroom and ModuleClassroom are the child rooms'
// plan roles.
const (
	ModuleNursery   ModuleRole = "nursery"
	ModulePlayroom  ModuleRole = "playroom"
	ModuleClassroom ModuleRole = "classroom"
)

// minNurseryBeds is the baby bed count below which the game scores a room
// no Nursery at all.
const minNurseryBeds = 2

// ChildFurniture is Count pieces of a role's furniture, each any one of
// Defs (the first the catalog makes available is placed).
type ChildFurniture struct {
	Defs  []string
	Count int
}

// ChildRoomNeed is the room one role is owed and what furnishes it.
type ChildRoomNeed struct {
	Role      RoomRole
	Module    ModuleRole
	Furniture []ChildFurniture
}

// ChildRoomDefinitions is every definition a child room names, sorted, for
// the planning catalog read.
func ChildRoomDefinitions() []string {
	var out []string
	for _, defs := range [][]string{NurseryBedDefinitions, PlayroomToyDefinitions, PlayroomDecoDefinitions, ClassroomBoardDefs, ClassroomDeskDefs} {
		for _, d := range defs {
			if !slices.Contains(out, d) {
				out = append(out, d)
			}
		}
	}
	slices.Sort(out)
	return out
}

// ChildRoomNeeds are the child rooms the pawns owe, in role order: a
// nursery (beds for every newborn and baby, at least the game's two) while
// one lives, a playroom while a baby or child does, a classroom (a desk per
// child and one blackboard) while a child does. Pawns whose developmental
// stage is unknown owe nothing.
func ChildRoomNeeds(pawns []WorkPawn) []ChildRoomNeed {
	var babies, toddlers, children int
	for _, p := range pawns {
		bt, ok := p.Biotech.Value()
		if !ok {
			continue
		}
		stage, ok := bt.DevelopmentalStage.Value()
		if !ok {
			continue
		}
		switch stage {
		case "Newborn":
			babies++
		case "Baby":
			babies++
			toddlers++
		case "Child":
			children++
			toddlers++
		}
	}
	var out []ChildRoomNeed
	if babies > 0 {
		out = append(out, ChildRoomNeed{Role: RoomRoleNursery, Module: ModuleNursery, Furniture: []ChildFurniture{{NurseryBedDefinitions, max(babies, minNurseryBeds)}}})
	}
	if toddlers > 0 {
		out = append(out, ChildRoomNeed{Role: RoomRolePlayroom, Module: ModulePlayroom, Furniture: []ChildFurniture{{PlayroomToyDefinitions, 1}, {PlayroomDecoDefinitions, 1}}})
	}
	if children > 0 {
		out = append(out, ChildRoomNeed{Role: RoomRoleClassroom, Module: ModuleClassroom, Furniture: []ChildFurniture{{ClassroomBoardDefs, 1}, {ClassroomDeskDefs, children}}})
	}
	return out
}

// childPiece is one resolved furniture entry: the definition placed (first
// available with a known footprint), every def that counts towards it, and
// how many are wanted.
type childPiece struct {
	Piece InteriorPieceDef
	Defs  []string
	Count int
}

// resolve picks each furniture entry's definition from the catalog; false
// when an entry has none available with a known footprint (the room cannot
// be sized or furnished from unknown facts).
func (n ChildRoomNeed) resolve(defs []FurnitureDefinition) ([]childPiece, bool) {
	out := make([]childPiece, 0, len(n.Furniture))
	for _, f := range n.Furniture {
		found := false
		for _, name := range f.Defs {
			for _, d := range defs {
				if d.Name != name {
					continue
				}
				available, ak := d.Available.Value()
				size, sk := d.Size.Value()
				if ak && available && sk && size.Width > 0 && size.Height > 0 {
					out = append(out, childPiece{Piece: InteriorPieceDef{Def: name, Size: domain.Cell{X: size.Width, Z: size.Height}}, Defs: f.Defs, Count: f.Count})
					found = true
				}
				break
			}
			if found {
				break
			}
		}
		if !found {
			return nil, false
		}
	}
	return out, true
}

// ChildRoomShape is a room the plan must grow: its role and the footprints
// it must hold.
type ChildRoomShape struct {
	Module ModuleRole
	Pieces []PieceCount
}

// PieceCount is Count pieces of one footprint.
type PieceCount struct {
	Size  domain.Cell
	Count int
}

func (n ChildRoomNeed) shape(defs []FurnitureDefinition) (ChildRoomShape, bool) {
	pieces, ok := n.resolve(defs)
	if !ok {
		return ChildRoomShape{}, false
	}
	s := ChildRoomShape{Module: n.Module}
	for _, p := range pieces {
		i := slices.IndexFunc(s.Pieces, func(c PieceCount) bool { return c.Size == p.Piece.Size })
		if i < 0 {
			s.Pieces = append(s.Pieces, PieceCount{Size: p.Piece.Size})
			i = len(s.Pieces) - 1
		}
		s.Pieces[i].Count += p.Count
	}
	return s, true
}

// ChildRoomsOwed are the shapes of the owed child rooms the plan holds no
// fitting room for; empty while every owed room is planned or its
// furniture is unknown.
func ChildRoomsOwed(plan LayoutPlan, needs []ChildRoomNeed, defs []FurnitureDefinition) []ChildRoomShape {
	var out []ChildRoomShape
	for _, n := range needs {
		s, ok := n.shape(defs)
		if !ok {
			continue
		}
		if _, planned := plan.ChildRoomFor(s); !planned {
			out = append(out, s)
		}
	}
	return out
}

// ChildRoomStepKind is the next child room step.
type ChildRoomStepKind string

const (
	// ChildRoomNone: nothing is due, or a fact is unknown.
	ChildRoomNone ChildRoomStepKind = ""
	// ChildRoomShell: raise the walls and door of Room.
	ChildRoomShell ChildRoomStepKind = "shell"
	// ChildRoomPlace: place Piece in Room.
	ChildRoomPlace ChildRoomStepKind = "place"
)

// ChildRoomStep is one bounded step towards an owed child room.
type ChildRoomStep struct {
	Kind  ChildRoomStepKind
	Need  ChildRoomNeed
	Room  LayoutRoom
	Piece InteriorPiece
}

// Owed reports whether the planner can act on the step now.
func (s ChildRoomStep) Owed() bool {
	return s.Kind == ChildRoomShell || s.Kind == ChildRoomPlace
}

// NextChildRoomStep picks the first owed child room's next step from the
// plan, the room census, the colony's buildings and the definitions. A role
// whose plan room is missing (the layout review owes it) or whose
// furniture is unresolved is passed over, as is a standing room that holds
// every piece or has no free slot.
func NextChildRoomStep(plan LayoutPlan, rooms RoomObservation, built []CurrentBuilding, needs []ChildRoomNeed, defs []FurnitureDefinition) ChildRoomStep {
	taken := map[domain.Cell]bool{}
	for _, b := range built {
		for _, c := range b.Cells {
			taken[c] = true
		}
	}
	for _, n := range needs {
		shape, ok := n.shape(defs)
		if !ok {
			continue
		}
		room, ok := plan.ChildRoomFor(shape)
		if !ok {
			continue
		}
		if _, standing := PlannedRoomStanding(room, rooms); !standing {
			return ChildRoomStep{Kind: ChildRoomShell, Need: n, Room: room}
		}
		pieces, _ := n.resolve(defs)
		in, rok := InteriorRoomFromLayout(room)
		if !rok {
			continue
		}
		for _, p := range pieces {
			if standingChildPieces(room, p.Defs, built) >= p.Count {
				continue
			}
			plan, ok := PlanInterior(in, p.Piece)
			if !ok {
				continue
			}
			for _, slot := range plan.Pieces {
				if slot.Def != p.Piece.Def || cellsTaken(slot.Rect, taken) {
					continue
				}
				return ChildRoomStep{Kind: ChildRoomPlace, Need: n, Room: room, Piece: slot}
			}
		}
	}
	return ChildRoomStep{}
}

func cellsTaken(r Rectangle, taken map[domain.Cell]bool) bool {
	for _, c := range rectCells(r) {
		if taken[c] {
			return true
		}
	}
	return false
}

// standingChildPieces counts the buildings of any of defs inside the room's
// interior.
func standingChildPieces(r LayoutRoom, defs []string, built []CurrentBuilding) int {
	n := 0
	for _, b := range built {
		if len(b.Cells) > 0 && rectInside(r.Interior, cellsRectangle(b.Cells)) && slices.Contains(defs, b.Building.Definition()) {
			n++
		}
	}
	return n
}

// Method names the step's method: per room and slot, so the shell and each
// piece are staged once per goal epoch.
func (s ChildRoomStep) Method() string {
	in := s.Room.Interior
	if s.Kind == ChildRoomPlace {
		return fmt.Sprintf("child-room-place-%d-%d-%s", in.X, in.Z, s.Piece.Slot)
	}
	return fmt.Sprintf("child-room-shell-%d-%d", in.X, in.Z)
}
