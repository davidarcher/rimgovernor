package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Staging the Biotech child rooms: the nursery,
// playroom and classroom. A room role is the game's own score over the
// room's contents (Rooms wiki, Roles): a Nursery needs at least two baby
// beds (a crib or a baby sleeping spot) and no other bed, a Playroom scores
// each toy box and baby decoration and a Classroom each blackboard and
// school desk, both only while the room holds no humanlike bed. The
// planner owes a role's room while a pawn of the matching developmental
// stage lives in the colony: the plan grows a room sized to the furniture
// (GrowChildRoom), the sleeping planner reconciles it to the plan and the
// furniture at the template's slots (NextChildRoomStep, ReconcileRoom). Footprints are the
// native definition catalog's, never constants here. Using the rooms
// (feeding, play, lessons) is the next children's.

// FurnitureRole is a room-role furniture role the definition catalog's def
// rows give each definition (the PlanningDefinition view's RoomRoles); a room's furniture is whichever catalog definitions carry the
// role, never a name list here.
type FurnitureRole string

// The roles the native catalog computes.
const (
	RoleBabyBed              FurnitureRole = "BabyBed"
	RoleToy                  FurnitureRole = "Toy"
	RoleDecoration           FurnitureRole = "Decoration"
	RoleBoard                FurnitureRole = "Board"
	RoleDesk                 FurnitureRole = "Desk"
	RoleDeathrestCasket      FurnitureRole = "DeathrestCasket"
	RoleDeathrestAccelerator FurnitureRole = "DeathrestAccelerator"
)

// FurnitureRoles are every role a planner places furniture by.
var FurnitureRoles = []FurnitureRole{RoleBabyBed, RoleToy, RoleDecoration, RoleBoard, RoleDesk, RoleDeathrestCasket, RoleDeathrestAccelerator}

// HasRole reports whether the catalog assigns the definition the role.
func (d FurnitureDefinition) HasRole(role FurnitureRole) bool {
	return slices.Contains(d.Roles, string(role))
}

// PlannedNursery, PlannedPlayroom and PlannedClassroom are the child rooms'
// plan roles.
const (
	PlannedNursery   PlannedRole = "nursery"
	PlannedPlayroom  PlannedRole = "playroom"
	PlannedClassroom PlannedRole = "classroom"
	// PlannedDeathrestChamber is the deathrest chamber's plan role.
	PlannedDeathrestChamber PlannedRole = "deathrest-chamber"
)

// minNurseryBeds is the baby bed count below which the game scores a room
// no Nursery at all.
const minNurseryBeds = 2

// ChildFurniture is Count pieces of a role's furniture, each any one
// definition the catalog gives Role, or any of Defs (the first available
// with a known footprint, by name, is placed). An Optional entry the catalog has no
// available definition for is left out; any other leaves the room unowed
// until the catalog has one.
type ChildFurniture struct {
	// Role selects the catalog definitions carrying the role; Defs instead
	// names one definition the game itself requires (the worship room's
	// buildings, read from the ideoligion).
	Role     FurnitureRole
	Defs     []string
	Count    int
	Optional bool
}

// ChildRoomNeed is the room one role is owed and what furnishes it.
type ChildRoomNeed struct {
	Role      RoomRole
	Module    PlannedRole
	Furniture []ChildFurniture
}

// ChildRoomNeeds are the child rooms the pawns owe, in role order: a
// nursery (beds for every newborn and baby, at least the game's two) while
// one lives, a playroom while a baby or child does, a classroom (a desk per
// child and one blackboard) while a child does, and a deathrest chamber (a
// casket per deathrester, and the accelerators its deathrest capacity
// beyond the casket allows) while a deathrester does. Pawns whose
// developmental stage or deathrest is unknown owe nothing.
func ChildRoomNeeds(pawns []WorkPawn) []ChildRoomNeed {
	var babies, toddlers, children, caskets, accelerators int
	for _, p := range pawns {
		bt, ok := p.Biotech.Value()
		if !ok {
			continue
		}
		if d, known := bt.Deathrest.Value(); known && d != nil {
			caskets++
			if capacity, ok := d.Capacity.Value(); ok && capacity > 1 {
				accelerators += capacity - 1
			}
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
		out = append(out, ChildRoomNeed{Role: RoomRoleNursery, Module: PlannedNursery, Furniture: []ChildFurniture{{Role: RoleBabyBed, Count: max(babies, minNurseryBeds)}}})
	}
	if toddlers > 0 {
		out = append(out, ChildRoomNeed{Role: RoomRolePlayroom, Module: PlannedPlayroom, Furniture: []ChildFurniture{{Role: RoleToy, Count: 1}, {Role: RoleDecoration, Count: 1}}})
	}
	if children > 0 {
		out = append(out, ChildRoomNeed{Role: RoomRoleClassroom, Module: PlannedClassroom, Furniture: []ChildFurniture{{Role: RoleBoard, Count: 1}, {Role: RoleDesk, Count: children}}})
	}
	if caskets > 0 {
		out = append(out, ChildRoomNeed{Role: RoomRoleDeathrestChamber, Module: PlannedDeathrestChamber, Furniture: []ChildFurniture{{Role: RoleDeathrestCasket, Count: caskets}, {Role: RoleDeathrestAccelerator, Count: accelerators, Optional: true}}})
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

// resolve picks each furniture entry's definition from the catalog: the
// first by name that carries the role, is available and has a known
// footprint. False when a required entry has none (the room cannot be
// sized or furnished from unknown facts).
func (n ChildRoomNeed) resolve(defs []FurnitureDefinition) ([]childPiece, bool) {
	out := make([]childPiece, 0, len(n.Furniture))
	for _, f := range n.Furniture {
		if f.Count <= 0 {
			continue
		}
		var piece childPiece
		found := false
		for _, d := range defs {
			if f.Role != "" && !d.HasRole(f.Role) || f.Role == "" && !slices.Contains(f.Defs, d.Name) {
				continue
			}
			piece.Defs = append(piece.Defs, d.Name)
			available, ak := d.Available.Value()
			size, sk := d.Size.Value()
			if ak && available && sk && size.Width > 0 && size.Height > 0 && (!found || d.Name < piece.Piece.Def) {
				piece.Piece = InteriorPieceDef{Def: d.Name, Size: domain.Cell{X: size.Width, Z: size.Height}}
				found = true
			}
		}
		switch {
		case found:
			piece.Count = f.Count
			out = append(out, piece)
		case !f.Optional:
			return nil, false
		}
	}
	return out, true
}

// ChildRoomShape is a room the plan must grow: its role and the footprints
// it must hold.
type ChildRoomShape struct {
	Module PlannedRole
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
	// ChildRoomReconcile: the room differs from the plan and the role's template
	// (Template: the furniture the role scores): the build side reconciles it
	// (ReconcileRoom). The room's state is whatever the diff leaves; there is no
	// shell or place step.
	ChildRoomReconcile ChildRoomStepKind = "reconcile"
)

// ChildRoomStep is one bounded step towards an owed child room.
type ChildRoomStep struct {
	Kind ChildRoomStepKind
	Need ChildRoomNeed
	Room PlannedRoom
	// Template is the wanted furniture: each role's standing pieces where they
	// stand, the missing ones in free template slots.
	Template []WantedPiece
}

// Owed reports whether the planner can act on the step now.
func (s ChildRoomStep) Owed() bool { return s.Kind == ChildRoomReconcile }

// NextChildRoomStep picks the first owed child room whose ground differs from
// its plan and template. A role whose plan room is missing (the layout review
// owes it) or whose furniture is unresolved is passed over, as is a room whose
// ring, doors and furniture match (a piece with no free slot is not wanted).
func NextChildRoomStep(plan LayoutPlan, rooms RoomObservation, ground GroundCensus, built []CurrentBuilding, needs []ChildRoomNeed, defs []FurnitureDefinition) ChildRoomStep {
	for _, n := range needs {
		shape, ok := n.shape(defs)
		if !ok {
			continue
		}
		room, ok := plan.ChildRoomFor(shape)
		if !ok {
			continue
		}
		pieces, _ := n.resolve(defs)
		in, rok := InteriorRoomFromLayout(room, rooms.Shapes)
		if !rok {
			continue
		}
		taken := map[domain.Cell]bool{}
		for _, b := range built {
			for _, c := range b.Cells {
				taken[c] = true
			}
		}
		var template []WantedPiece
		absent := false
		for _, p := range pieces {
			count := 0
			for _, b := range built {
				if count < p.Count && len(b.Cells) > 0 && rectInside(room.Interior, cellsRectangle(b.Cells)) && slices.Contains(p.Defs, b.Building.Definition()) {
					r := cellsRectangle(b.Cells)
					template = append(template, WantedPiece{DefName: b.Building.Definition(), Minimum: domain.Cell{X: r.X, Z: r.Z}, Maximum: domain.Cell{X: r.X + r.Width - 1, Z: r.Z + r.Height - 1}})
					count++
				}
			}
			if count >= p.Count {
				continue
			}
			planned, ok := PlanInterior(in, p.Piece)
			if !ok {
				continue
			}
			for _, slot := range planned.Pieces {
				if count >= p.Count || slot.Def != p.Piece.Def || cellsTaken(slot.Rect, taken) {
					continue
				}
				for _, c := range rectCells(slot.Rect) {
					taken[c] = true
				}
				template = append(template, WantedPiece{DefName: slot.Def, Minimum: domain.Cell{X: slot.Rect.X, Z: slot.Rect.Z}, Maximum: domain.Cell{X: slot.Rect.X + slot.Rect.Width - 1, Z: slot.Rect.Z + slot.Rect.Height - 1}, Slot: slot.Slot, Size: slot.Size, Rot: slot.Rot})
				absent = true
				count++
			}
		}
		if absent || !plan.GroundMatches(room, ground) {
			return ChildRoomStep{Kind: ChildRoomReconcile, Need: n, Room: room, Template: template}
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
func standingChildPieces(r PlannedRoom, defs []string, built []CurrentBuilding) int {
	n := 0
	for _, b := range built {
		if len(b.Cells) > 0 && rectInside(r.Interior, cellsRectangle(b.Cells)) && slices.Contains(defs, b.Building.Definition()) {
			n++
		}
	}
	return n
}
