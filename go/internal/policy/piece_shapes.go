package policy

import "fmt"

// PieceShapes is the plannable shape of every buildable definition, as the
// catalog rows give it (DefinitionCatalog.PieceShapes): its size and
// interaction cell offset at rotation North, and the family it shares slots
// with, which is the room role the game scores the building into (the
// definition's workTableRoomRole, or the bedroom or hospital role of a
// humanlike bed). It is a threaded value on the room observation, carried to
// InteriorRoom and InteriorFrame, never a global.
type PieceShapes map[string]InteriorPieceDef

// Get is a definition's shape; false when the catalog has no buildable row
// for it.
func (s PieceShapes) Get(def string) (InteriorPieceDef, bool) {
	shape, ok := s[def]
	return shape, ok
}

// standing is the shape of a building standing in a room: a definition no
// buildable row holds (a ruin, a natural edifice) is no plannable piece and
// comes back with only its name, in no family.
func (s PieceShapes) standing(def string) InteriorPieceDef {
	if shape, ok := s[def]; ok {
		return shape
	}
	return InteriorPieceDef{Def: def}
}

// Accepts reports whether a definition may take this slot: the slot's own
// definition, or a member of its family with the same footprint (size and
// interaction offset), which stays regular and role-correct in the slot.
func (p InteriorPiece) Accepts(shapes PieceShapes, def string) bool {
	if def == p.Def {
		return true
	}
	a, aok := shapes.Get(p.Def)
	b, bok := shapes.Get(def)
	return aok && bok && a.Family != "" && a.Family == b.Family && a.Size == b.Size && sameInteraction(a.Interaction, b.Interaction)
}

// templatePieces are the furniture the interior templates plan beside the
// piece they are asked for: the family each must be in (empty: any) and
// whether it is a bench a worker uses from the open floor in front of it.
// A catalog missing one cannot lay a template out, which Validate names.
var templatePieces = []struct {
	Def    string
	Family RoomRole
	Bench  bool
}{
	{"Bed", RoomRoleBedroom, false},
	{"DoubleBed", RoomRoleBedroom, false},
	{"EndTable", "", false},
	{"Dresser", "", false},
	{"StandingLamp", "", false},
	{"ToolCabinet", "", false},
	{"ShelfSmall", "", false},
	{"VitalsMonitor", "", false},
	{SarcophagusDefinition, "", false},
	{KitchenStoveDefinition, RoomRoleKitchen, true},
	{workshopBenchDef, RoomRoleWorkshop, true},
	{ResearchBenchDefinition, RoomRoleLaboratory, true},
}

// Validate checks every shape is a footprint a template can lay out (a
// positive size) and that the furniture the templates plan is present, in
// its family and, for a bench, worked from the floor in front of it.
func (s PieceShapes) Validate() error {
	for def, shape := range s {
		if shape.Def != def || shape.Size.X < 1 || shape.Size.Z < 1 {
			return fmt.Errorf("piece shape of %s is %q %dx%d", def, shape.Def, shape.Size.X, shape.Size.Z)
		}
	}
	for _, want := range templatePieces {
		shape, ok := s[want.Def]
		if !ok {
			return fmt.Errorf("interior templates plan %s, which the catalog has no buildable row for", want.Def)
		}
		if want.Family != "" && shape.Family != want.Family {
			return fmt.Errorf("interior templates plan %s as a %s piece, its row puts it in family %q", want.Def, want.Family, shape.Family)
		}
		if want.Bench && !frontInteraction(shape) {
			return fmt.Errorf("interior templates plan %s as a bench, its row has no interaction cell in front of it", want.Def)
		}
	}
	return nil
}
