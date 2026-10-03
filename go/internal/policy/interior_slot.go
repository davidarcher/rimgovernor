package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// InteriorPieceDef is the shape of a definition a template can plan: its
// size and interaction cell offset at rotation North, and the slot family it
// shares slots with (#819, #820). A family is the room role the game scores
// the building into: kitchen stoves, workshop benches, laboratory benches,
// the beds of a bedroom. The catalog rows put a building in its family
// (PieceShapes); a building in no family, like a butchery, whose blood filth
// must never take a kitchen or workshop slot (SeparationProtectedCells), has
// none.
type InteriorPieceDef struct {
	Def    string
	Size   domain.Cell
	Family RoomRole
	// Interaction is the interaction cell offset from the anchor at North;
	// nil for a definition without one.
	Interaction *domain.Cell
}

func sameInteraction(a, b *domain.Cell) bool {
	return a == b || a != nil && b != nil && *a == *b
}
