package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ManagePollution keeps wastepacks out of the open and the cleanup crew's
// area on the polluted ground (epic #1667, #1683). Wastepacks that are
// neither frozen nor inside an atomizer deteriorate into pollution (Biotech
// CompDissolution); a forbidden pack cannot be hauled at all. The goal is in
// deficit while any wastepack is exposed or forbidden, or the game counts a
// polluted cell outside the pollution-clear area; it settles when none is.
// The methods are designations and area edits, never the outcome (the pack
// frozen or atomized, the cell inside the area).
//
// It is a Standard whose target is no outstanding work, like RemoveBlight.
// The existing toxic-fallout shelter and crop-pollution handling are
// separate and untouched.
const ManagePollution ConcernID = "ManagePollution"

// Wastepack is one spawned wastepack stack with the game's own verdicts
// (CompDissolution.IsFrozen, InAtomizer, the item's forbidden flag). An
// unknown verdict is never read as false.
type Wastepack struct {
	ID, Definition                string
	Cell                          domain.Cell
	Frozen, InAtomizer, Forbidden domain.Fact[bool]
}

// PollutionFacts is the Biotech pollution read the goal works from.
// UncoveredCells counts the polluted cells outside the pollution-clear area
// (the game's grid, whole map); unknown when native did not say.
type PollutionFacts struct {
	Wastepacks     []Wastepack
	UncoveredCells domain.Fact[uint32]
}

// Exposed reports a pack known to be neither frozen nor in an atomizer; its
// second result is false when either verdict is unknown and the pack is not
// known protected.
func (w Wastepack) Exposed() (exposed, known bool) {
	frozen, fk := w.Frozen.Value()
	atomized, ak := w.InAtomizer.Value()
	if fk && frozen || ak && atomized {
		return false, true
	}
	return true, fk && ak
}

// Held reports a pack known to be forbidden.
func (w Wastepack) Held() bool {
	forbidden, known := w.Forbidden.Value()
	return known && forbidden
}

// PollutionDeficit is the binary ManagePollution signal. Any known exposed or
// forbidden pack, or polluted cell outside the area, is a deficit; otherwise
// the answer is no deficit only when every verdict is known.
func PollutionDeficit(facts domain.Fact[PollutionFacts]) domain.Fact[bool] {
	f, known := facts.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	complete := true
	for _, w := range f.Wastepacks {
		if w.Held() {
			return domain.Known(true)
		}
		exposed, ok := w.Exposed()
		if exposed && ok {
			return domain.Known(true)
		}
		if _, forbiddenKnown := w.Forbidden.Value(); !ok || !forbiddenKnown {
			complete = false
		}
	}
	uncovered, ok := f.UncoveredCells.Value()
	if ok && uncovered > 0 {
		return domain.Known(true)
	}
	if !ok || !complete {
		return domain.Unknown[bool]()
	}
	return domain.Known(false)
}

// PollutionWork is what ManagePollution orders this cycle: packs to allow
// (a forbidden pack cannot be hauled), packs to haul to storage, and the
// polluted cells to put in the pollution-clear area.
type PollutionWork struct {
	Allow, Haul []Wastepack
	Area        []domain.Cell
}

func (w PollutionWork) Empty() bool { return len(w.Allow) == 0 && len(w.Haul) == 0 && len(w.Area) == 0 }

// SelectPollutionWork derives the work. A forbidden pack is allowed first and
// hauled on a later cycle, once it stands allowed. A pack in claimed (an
// earlier haul of it ended unsuccessful, so no stockpile took it) is not
// re-ordered; packs are ordered by id. polluted is the observed window's
// polluted cells (PollutedWindowCells); the area edit is offered only while
// the game counts polluted cells outside the area, and setting a cell already
// in the area changes nothing.
func SelectPollutionWork(facts PollutionFacts, claimed map[string]bool, polluted []domain.Cell) PollutionWork {
	var work PollutionWork
	for _, w := range facts.Wastepacks {
		if w.ID == "" {
			continue
		}
		if w.Held() {
			work.Allow = append(work.Allow, w)
			continue
		}
		if exposed, known := w.Exposed(); exposed && known && !claimed[w.ID] {
			work.Haul = append(work.Haul, w)
		}
	}
	byID := func(rows []Wastepack) {
		sort.Slice(rows, func(i, j int) bool { return rows[i].ID < rows[j].ID })
	}
	byID(work.Allow)
	byID(work.Haul)
	if uncovered, known := facts.UncoveredCells.Value(); known && uncovered > 0 {
		work.Area = polluted
	}
	return work
}

// PollutedWindowCells is the polluted cells the planning window shows,
// sorted. Cells without a polluted verdict (fogged) are not polluted ground
// the crew can be sent to.
func PollutedWindowCells(cells []SiteCell) []domain.Cell {
	out := []domain.Cell{}
	for _, c := range cells {
		if polluted, ok := c.Polluted.Value(); ok && polluted {
			out = append(out, c.Cell)
		}
	}
	sort.Slice(out, func(i, j int) bool { return extentCellLess(out[i], out[j]) })
	return out
}
