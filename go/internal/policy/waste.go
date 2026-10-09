package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// WasteState mirrors the native WasteLocation the wire carries for one item:
// exposed (a containment candidate), relocated (already hauled to a
// separated stockpile) or buried (already interred in a grave).
type WasteState string

const (
	WasteExposed   WasteState = "exposed"
	WasteRelocated WasteState = "relocated"
	WasteBuried    WasteState = "buried"
)

// WasteItem is one native waste census row
// (thingId/kind/eligible/state). Eligible is native
// authority's own eligibility judgment (destination separation, protection,
// player policy); Go never second-guesses it, only filters on it.
type WasteItem struct {
	ID       string
	Kind     string
	State    WasteState
	Eligible bool
	Cell     domain.Cell
	// CorpseOf is a corpse's inner pawn class; empty for anything else.
	CorpseOf domain.CorpseOf
	// RotStage is the item's rot stage; empty when it does not rot or the
	// census did not say.
	RotStage domain.RotStage
	// Grave is the holding grave's ID for a buried corpse.
	Grave string
	// EverBuriedInSarcophagus is the corpse's vanilla flag: a re-burial fires no
	// memory, so it is never owed a sarcophagus.
	EverBuriedInSarcophagus bool
}
