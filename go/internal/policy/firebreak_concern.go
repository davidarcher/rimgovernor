package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// MaintainFirebreak keeps the firebreak ring (#1536) clear: its cut cells
// free of standing plants and its wooden ruins taken down. The goal is in
// deficit while FirebreakOwed finds work; an order is the method, so the
// goal settles on the census, never on a receipt.
const MaintainFirebreak ConcernID = "MaintainFirebreak"

// FirebreakWork is what the ring owes now: the cut cells where an
// undesignated plant stands and the wooden ruins with no deconstruction
// designated.
type FirebreakWork struct {
	Cut         []domain.Cell
	Deconstruct []domain.Cell
}

// Owed reports whether any ring work stands.
func (w FirebreakWork) Owed() bool { return len(w.Cut)+len(w.Deconstruct) > 0 }

// FirebreakOwed is the ring work plan leaves: a cut cell where standing
// reports an undesignated plant, and a planned wooden ruin that open still
// reports undesignated. Paved cells are MaintainFlooring's; the plan's
// order is kept.
func FirebreakOwed(plan FirebreakPlan, standing, open map[domain.Cell]bool) FirebreakWork {
	w := FirebreakWork{Cut: []domain.Cell{}, Deconstruct: []domain.Cell{}}
	for _, c := range plan.Cells {
		if c.Treatment == FirebreakCut && standing[c.Cell] {
			w.Cut = append(w.Cut, c.Cell)
		}
	}
	for _, c := range plan.Deconstruct {
		if open[c] {
			w.Deconstruct = append(w.Deconstruct, c)
		}
	}
	return w
}
