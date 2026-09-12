package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"reflect"
	"testing"
)

func TestStartingSuppliesOnlyOriginalCohort(t *testing.T) {
	a, b, later := domain.Cell{X: 2, Z: 1}, domain.Cell{X: 1, Z: 2}, domain.Cell{X: 3, Z: 3}
	s := StartingSupplies{}
	for _, test := range []struct {
		name        string
		cells       domain.Fact[[]domain.Cell]
		pending     []domain.Cell
		initialized bool
		need        domain.Fact[bool]
	}{
		{"missing first read", domain.Unknown[[]domain.Cell](), nil, false, domain.Unknown[bool]()},
		{"first cohort sorted", domain.Known([]domain.Cell{b, a}), []domain.Cell{a, b}, true, domain.Known(true)},
		{"one released and later forbid", domain.Known([]domain.Cell{b, later}), []domain.Cell{b}, true, domain.Known(true)},
		{"unavailable retains pending", domain.Unknown[[]domain.Cell](), []domain.Cell{b}, true, domain.Unknown[bool]()},
		{"released cell re-forbidden", domain.Known([]domain.Cell{a, b, later}), []domain.Cell{b}, true, domain.Known(true)},
		{"all original released", domain.Known([]domain.Cell{a, later}), nil, true, domain.Known(false)},
		{"later forbids cannot reopen", domain.Known([]domain.Cell{a, b, later}), nil, true, domain.Known(false)},
	} {
		t.Run(test.name, func(t *testing.T) {
			var need domain.Fact[bool]
			var err error
			s, need, err = ReviewStartingSupplies(test.cells, s)
			if err != nil || s.Initialized != test.initialized || !reflect.DeepEqual(s.Pending, test.pending) || need != test.need {
				t.Fatal(s, need, err)
			}
		})
	}
	// A successful empty first read is still an initialized cohort.
	s, _, err := ReviewStartingSupplies(domain.Known([]domain.Cell{}), StartingSupplies{})
	s, need, err := ReviewStartingSupplies(domain.Known([]domain.Cell{a}), s)
	if err != nil || len(s.Pending) != 0 || need != domain.Known(false) {
		t.Fatal(s, need, err)
	}
}

func TestStartingSuppliesOwnsInputsAndRejectsInvalidHistory(t *testing.T) {
	cells := []domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 2}}
	s, _, err := ReviewStartingSupplies(domain.Known(cells), StartingSupplies{})
	if err != nil {
		t.Fatal(err)
	}
	cells[0].X = 30
	if s.Pending[0].X != 1 {
		t.Fatal("aliased observation")
	}
	next, _, err := ReviewStartingSupplies(domain.Unknown[[]domain.Cell](), s)
	if err != nil {
		t.Fatal(err)
	}
	next.Pending[0].X = 40
	if s.Pending[0].X != 1 {
		t.Fatal("aliased history")
	}
	for _, bad := range []StartingSupplies{
		{Pending: []domain.Cell{{X: 1, Z: 1}}},
		{Initialized: true, Pending: []domain.Cell{{X: 1, Z: 1}, {X: 1, Z: 1}}},
		{Initialized: true, Pending: []domain.Cell{{X: 2, Z: 2}, {X: 1, Z: 1}}},
		{Initialized: true, Pending: []domain.Cell{{X: -1}}},
		{Initialized: true, Pending: []domain.Cell{{X: 4096}}},
	} {
		if _, _, err = ReviewStartingSupplies(domain.Unknown[[]domain.Cell](), bad); err == nil {
			t.Fatal("invalid history accepted", bad)
		}
	}
	if _, _, err = ReviewStartingSupplies(domain.Known([]domain.Cell{{}, {}}), StartingSupplies{}); err == nil {
		t.Fatal("duplicate census accepted")
	}
}
