package policy

import "fmt"

// PieceShapes is the plannable shape of every buildable definition, as the
// catalog rows give it (DefinitionCatalog.PieceShapes): its size and
// interaction cell offset at rotation North, and the family it shares slots
// with, which is the room role the game scores the building into (the
// definition's workTableRoomRole, or the bedroom or hospital role of a
// humanlike bed); and the furniture the room planners place, chosen by rules
// over the same rows (RoomFurniture). It is a threaded value on the room
// observation and the colony projection, carried to InteriorRoom and
// InteriorFrame, never a global.
type PieceShapes struct {
	Defs      map[string]InteriorPieceDef
	Furniture RoomFurniture
}

// Get is a definition's shape; false when the catalog has no buildable row
// for it.
func (s PieceShapes) Get(def string) (InteriorPieceDef, bool) {
	shape, ok := s.Defs[def]
	return shape, ok
}

// standing is the shape of a building standing in a room: a definition no
// buildable row holds (a ruin, a natural edifice) is no plannable piece and
// comes back with only its name, in no family.
func (s PieceShapes) standing(def string) InteriorPieceDef {
	if shape, ok := s.Defs[def]; ok {
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

// WorkedFromFront reports whether a worker uses the piece from the open floor
// in front of it at rotation North, as a bench row needs.
func (d InteriorPieceDef) WorkedFromFront() bool { return frontInteraction(d) }

// templatePiece is a definition the interior templates plan: the family it
// must be in (empty: any) and whether it is a bench a worker uses from the
// open floor in front of it.
type templatePiece struct {
	Def    string
	Family RoomRole
	Bench  bool
}

// templatePieces are the furniture the interior templates plan that no rule
// of RoomFurniture names. A catalog missing one cannot lay a template out,
// which Validate names.
var templatePieces = []templatePiece{
	{standingLampDef, "", false},
	{workshopShelfDef, "", false},
	{shelterCampfireDef, "", false},
	{shelterCraftingDef, "", false},
}

// Validate checks every shape is a footprint a template can lay out (a
// positive size), that the furniture rules found a set the templates can lay
// out and that every definition they plan is present, in its family and, for
// a bench, worked from the floor in front of it.
func (s PieceShapes) Validate() error {
	for def, shape := range s.Defs {
		if shape.Def != def || shape.Size.X < 1 || shape.Size.Z < 1 {
			return fmt.Errorf("piece shape of %s is %q %dx%d", def, shape.Def, shape.Size.X, shape.Size.Z)
		}
	}
	if err := s.Furniture.Validate(); err != nil {
		return err
	}
	wants := []templatePiece{{s.Furniture.PrimaryBed(), RoomRoleBedroom, false}, {s.Furniture.CoupleBed(), RoomRoleBedroom, false}, {s.Furniture.Sarcophagus, "", false}}
	for role, def := range s.Furniture.Bench {
		wants = append(wants, templatePiece{def, role, true})
	}
	for _, link := range s.Furniture.Facilities() {
		wants = append(wants, templatePiece{link.Def, "", false})
	}
	if s.Furniture.AdvancedLab != "" {
		wants = append(wants, templatePiece{s.Furniture.AdvancedLab, RoomRoleLaboratory, true}, templatePiece{s.Furniture.Analyzer.Def, "", false})
	}
	wants = append(wants, templatePieces...)
	for _, w := range wants {
		shape, ok := s.Defs[w.Def]
		if !ok {
			return fmt.Errorf("interior templates plan %s, which the catalog has no buildable row for", w.Def)
		}
		if w.Family != "" && shape.Family != w.Family {
			return fmt.Errorf("interior templates plan %s as a %s piece, its row puts it in family %q", w.Def, w.Family, shape.Family)
		}
		if w.Bench && !frontInteraction(shape) {
			return fmt.Errorf("interior templates plan %s as a bench, its row has no interaction cell in front of it", w.Def)
		}
	}
	return nil
}
