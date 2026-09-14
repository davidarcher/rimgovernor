package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainWaste is the goal ported from waste_management.py: contain or bury
// exposed, eligible native waste (filth, junk, corpses) that would otherwise
// sit in the open, unlike MaintainCleanFacilities' upkeep filth or
// MaintainAnimalContainment's herd containment.
const MaintainWaste GoalID = "MaintainWaste"

// WasteState mirrors the native WasteLocation the wire carries for one item:
// exposed (a containment candidate), relocated (already hauled to a
// separated stockpile) or buried (already interred in a grave).
type WasteState string

const (
	WasteExposed   WasteState = "exposed"
	WasteRelocated WasteState = "relocated"
	WasteBuried    WasteState = "buried"
)

// WasteItem is one native waste census row, ported from waste_management.py's
// pending_items input shape (thingId/kind/eligible/state). Eligible is native
// authority's own eligibility judgment (destination separation, protection,
// player policy); Go never second-guesses it, only filters on it.
type WasteItem struct {
	ID       string
	Kind     string
	State    WasteState
	Eligible bool
	Cell     domain.Cell
}

// pendingWaste ports waste_management.py's pending_items: an exposed,
// eligible item is a containment/burial candidate. A relocated or buried item,
// or one native marked ineligible, is not.
func pendingWaste(items []WasteItem) []WasteItem {
	var out []WasteItem
	for _, item := range items {
		if item.Eligible && item.State == WasteExposed {
			out = append(out, item)
		}
	}
	return out
}

// WasteDeficit ports development_priorities.py's binary MaintainWaste
// deficit signal: unknown census stays unknown (absence is never evidence of
// recovery), otherwise deficit is simply "any pending item remains".
func WasteDeficit(items domain.Fact[[]WasteItem]) domain.Fact[bool] {
	rows, known := items.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(len(pendingWaste(rows)) > 0)
}

// WastePawn mirrors CleanCandidateFacts, narrowed to waste_management.py's
// compile_method exclusion set: dead, downed, drafted or mentally broken
// pawns never become haul/burial candidates. Unlike cleaning, waste's own
// native WorkGiver scan carries no work-type-enabled or health gate to check
// here; the native preview at dispatch still owns final acceptance.
type WastePawn struct {
	ID                                 PawnID
	Dead, Downed, Drafted, MentalState domain.Fact[bool]
}

// SelectWasteMethod ports waste_management.py's compile_method pairing:
// among pending (exposed, eligible) items, corpses sort first, then lowest
// thing ID; among eligible pawns (known not dead, downed, drafted or
// mentally broken), lowest pawn ID. Unlike compile_method's rotating
// preview-and-refuse cursor over the first 8 (item, pawn) pairs -- a
// dispatch-retry concern belonging to whatever planner drives this, not
// selection -- this only proposes the single best pair; the native preview
// immediately before dispatch still owns whether the haul or burial job is
// actually accepted.
func SelectWasteMethod(items []WasteItem, pawns []WastePawn) (WasteItem, PawnID, bool) {
	pending := pendingWaste(items)
	if len(pending) == 0 {
		return WasteItem{}, "", false
	}
	sort.Slice(pending, func(i, j int) bool {
		ci, cj := pending[i].Kind == "corpse", pending[j].Kind == "corpse"
		if ci != cj {
			return ci
		}
		return pending[i].ID < pending[j].ID
	})
	eligible := func(p WastePawn) bool {
		dead, dk := p.Dead.Value()
		downed, wk := p.Downed.Value()
		drafted, tk := p.Drafted.Value()
		mental, mk := p.MentalState.Value()
		if !dk || !wk || !tk || !mk {
			return false
		}
		return !dead && !downed && !drafted && !mental
	}
	var pool []WastePawn
	for _, p := range pawns {
		if eligible(p) {
			pool = append(pool, p)
		}
	}
	if len(pool) == 0 {
		return WasteItem{}, "", false
	}
	sort.Slice(pool, func(i, j int) bool { return pool[i].ID < pool[j].ID })
	return pending[0], pool[0].ID, true
}
