package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestFirebreakOwedRaisesMaintainFirebreak(t *testing.T) {
	f := stableRounds()
	f.FirebreakOwed = domain.Known(true)
	r := needs(t, f, RoundsLatches{})
	for _, g := range r.Concerns {
		if g.ID == MaintainFirebreak {
			return
		}
	}
	t.Fatal("owed firebreak raised no MaintainFirebreak goal", r.Concerns)
}

// Only cut cells with a standing plant are owed; paved cells belong to
// MaintainFlooring and a ruin already designated is left to its order.
func TestFirebreakOwedCutAndRuins(t *testing.T) {
	cut, pave, bare, ruin, held := domain.Cell{X: 1, Z: 1}, domain.Cell{X: 2, Z: 1}, domain.Cell{X: 3, Z: 1}, domain.Cell{X: 4, Z: 1}, domain.Cell{X: 5, Z: 1}
	plan := FirebreakPlan{
		Cells:       []FirebreakCell{{cut, FirebreakCut}, {pave, FirebreakPave}, {bare, FirebreakCut}, {ruin, FirebreakCut}, {held, FirebreakCut}},
		Deconstruct: []domain.Cell{ruin, held},
	}
	w := FirebreakOwed(plan, map[domain.Cell]bool{cut: true, pave: true}, map[domain.Cell]bool{ruin: true})
	if !slices.Equal(w.Cut, []domain.Cell{cut}) || !slices.Equal(w.Deconstruct, []domain.Cell{ruin}) || !w.Owed() {
		t.Fatal(w)
	}
	if FirebreakOwed(plan, nil, nil).Owed() {
		t.Fatal("clear ring owes work")
	}
}
