package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The shelter's slot names and bunk footprint. What the starter room holds is
// shelterWants (interior_region_roles.go): the region placer seats it.

const (
	shelterCraftingDef = "CraftingSpot"
	// shelterBunkSlotPrefix names every bunk slot ("bunk.1", "bunk.2", ...).
	shelterBunkSlotPrefix = "bunk."
)

// ShelterCoolerSlotPrefix names the passive cooler slots ("cooler.1", ...), a
// hot map's floor slot for the temperature planner.
const ShelterCoolerSlotPrefix = "cooler."

// shelterBunkSize is a bunk's North footprint: the 1x2 of a SleepingSpot, a
// Bed and a Bedroll.
var shelterBunkSize = domain.Cell{X: 1, Z: 2}

// IsBunk reports whether the piece is one of the shelter's bunk slots.
func (p InteriorPiece) IsBunk() bool {
	return len(p.Slot) > len(shelterBunkSlotPrefix) && p.Slot[:len(shelterBunkSlotPrefix)] == shelterBunkSlotPrefix
}
