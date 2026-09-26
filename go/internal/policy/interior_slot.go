package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// InteriorPieceDef is the shape of a definition a template can plan: its
// size and interaction cell offset at rotation North, and the slot family
// it shares slots with (#819, #820). A family is one room role: stoves
// (kitchen), workshop benches, research benches (laboratory). Butchery is
// in no family: its blood filth must never take a kitchen or workshop slot
// (SeparationProtectedCells).
type InteriorPieceDef struct {
	Def    string
	Size   domain.Cell
	Family string
	// Interaction is the interaction cell offset from the anchor at North;
	// nil for a definition without one.
	Interaction *domain.Cell
}

const (
	pieceFamilyStove    = "stove"
	pieceFamilyBench    = "bench"
	pieceFamilyResearch = "research"
	pieceFamilyBed      = "bed"
)

// interiorPieceDefs are the definitions templates plan by family. Every
// size and offset is checked against Core's
// ThingDefs_Buildings/Buildings_Production.xml (1.5/1.6): each is a
// BenchBase child with interactionCellOffset (0,0,-1), one cell in front
// of the anchor at North.
var interiorPieceDefs = func() map[string]InteriorPieceDef {
	front := domain.Cell{X: 0, Z: -1}
	out := map[string]InteriorPieceDef{}
	add := func(family string, size domain.Cell, defs ...string) {
		for _, d := range defs {
			out[d] = InteriorPieceDef{Def: d, Size: size, Family: family, Interaction: &front}
		}
	}
	add(pieceFamilyStove, domain.Cell{X: 3, Z: 1}, "FueledStove", "ElectricStove")
	add(pieceFamilyBench, domain.Cell{X: 3, Z: 1}, "TableStonecutter", "FueledSmithy", "ElectricSmithy", "HandTailoringBench", "ElectricTailoringBench", "TableMachining", "ElectricSmelter", "TableSculpting", "Brewery", "DrugLab")
	add(pieceFamilyBench, domain.Cell{X: 5, Z: 2}, "FabricationBench")
	add(pieceFamilyResearch, domain.Cell{X: 3, Z: 2}, "SimpleResearchBench")
	add(pieceFamilyResearch, domain.Cell{X: 5, Z: 2}, "HiTechResearchBench")
	// Beds have no interaction cell (Buildings_Furniture.xml, Royalty's
	// RoyalBed); the bedroom template plans the requested or standing one.
	bed := func(size domain.Cell, defs ...string) {
		for _, d := range defs {
			out[d] = InteriorPieceDef{Def: d, Size: size, Family: pieceFamilyBed}
		}
	}
	bed(domain.Cell{X: 1, Z: 2}, "Bed", "SleepingSpot")
	bed(domain.Cell{X: 2, Z: 2}, "DoubleBed", "RoyalBed", "DoubleSleepingSpot")
	return out
}()

// InteriorPieceDefFor is a definition's plannable shape; a definition no
// family holds comes back with only its name, and templates plan their
// own default for it.
func InteriorPieceDefFor(def string) InteriorPieceDef {
	if d, ok := interiorPieceDefs[def]; ok {
		return d
	}
	return InteriorPieceDef{Def: def}
}

func sameInteraction(a, b *domain.Cell) bool {
	return a == b || a != nil && b != nil && *a == *b
}

// Accepts reports whether a definition may take this slot: the slot's own
// definition, or a member of its family with the same footprint (size and
// interaction offset), which stays regular and role-correct in the slot.
func (p InteriorPiece) Accepts(def string) bool {
	if def == p.Def {
		return true
	}
	a, b := InteriorPieceDefFor(p.Def), InteriorPieceDefFor(def)
	return a.Family != "" && a.Family == b.Family && a.Size == b.Size && sameInteraction(a.Interaction, b.Interaction)
}
