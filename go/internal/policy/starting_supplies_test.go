package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"reflect"
	"testing"
)

func TestStartingSuppliesOnlyOriginalCohort(t *testing.T) {
	a := StartingSupply{Thing: "Thing_Pemmican1", Definition: "Pemmican", Cell: domain.Cell{X: 2, Z: 1}}
	b := StartingSupply{Thing: "Thing_WoodLog2", Definition: "WoodLog", Cell: domain.Cell{X: 1, Z: 2}}
	later := StartingSupply{Thing: "Thing_WoodLog3", Definition: "WoodLog", Cell: domain.Cell{X: 3, Z: 3}}
	// The same stack after a builder hauled it aside.
	bMoved := b
	bMoved.Cell = domain.Cell{X: 5, Z: 5}
	s := StartingSupplies{}
	for _, test := range []struct {
		name        string
		rows        domain.Fact[[]StartingSupply]
		pending     []StartingSupply
		initialized bool
		need        domain.Fact[bool]
	}{
		{"missing first read", domain.Unknown[[]StartingSupply](), nil, false, domain.Unknown[bool]()},
		{"first cohort sorted", domain.Known([]StartingSupply{b, a}), []StartingSupply{a, b}, true, domain.Known(true)},
		{"one released and later forbid", domain.Known([]StartingSupply{b, later}), []StartingSupply{b}, true, domain.Known(true)},
		{"unavailable retains pending", domain.Unknown[[]StartingSupply](), []StartingSupply{b}, true, domain.Unknown[bool]()},
		{"hauled aside keeps the stack at its new cell", domain.Known([]StartingSupply{bMoved, later}), []StartingSupply{bMoved}, true, domain.Known(true)},
		{"released stack re-forbidden", domain.Known([]StartingSupply{a, bMoved, later}), []StartingSupply{bMoved}, true, domain.Known(true)},
		{"all original released", domain.Known([]StartingSupply{a, later}), nil, true, domain.Known(false)},
		{"later forbids cannot reopen", domain.Known([]StartingSupply{a, b, later}), nil, true, domain.Known(false)},
	} {
		t.Run(test.name, func(t *testing.T) {
			var need domain.Fact[bool]
			var err error
			s, need, err = ReviewStartingSupplies(test.rows, s)
			if err != nil || s.Initialized != test.initialized || !reflect.DeepEqual(s.Pending, test.pending) || need != test.need {
				t.Fatal(s, need, err)
			}
		})
	}
	// A successful empty first read is still an initialized cohort.
	s, _, err := ReviewStartingSupplies(domain.Known([]StartingSupply{}), StartingSupplies{})
	if err != nil {
		t.Fatal(err)
	}
	s, need, err := ReviewStartingSupplies(domain.Known([]StartingSupply{a}), s)
	if err != nil || len(s.Pending) != 0 || need != domain.Known(false) {
		t.Fatal(s, need, err)
	}
}

func TestStartingSuppliesOwnsInputsAndRejectsInvalidHistory(t *testing.T) {
	row := func(id string, x int32) StartingSupply {
		return StartingSupply{Thing: id, Definition: "Steel", Cell: domain.Cell{X: x, Z: 1}}
	}
	rows := []StartingSupply{row("a", 1), row("b", 2)}
	s, _, err := ReviewStartingSupplies(domain.Known(rows), StartingSupplies{})
	if err != nil {
		t.Fatal(err)
	}
	rows[0].Cell.X = 30
	if s.Pending[0].Cell.X != 1 {
		t.Fatal("aliased observation")
	}
	next, _, err := ReviewStartingSupplies(domain.Unknown[[]StartingSupply](), s)
	if err != nil {
		t.Fatal(err)
	}
	next.Pending[0].Cell.X = 40
	if s.Pending[0].Cell.X != 1 {
		t.Fatal("aliased history")
	}
	for _, bad := range []StartingSupplies{
		{Pending: []StartingSupply{row("a", 1)}},
		{Initialized: true, Pending: []StartingSupply{row("a", 1), row("a", 1)}},
		{Initialized: true, Pending: []StartingSupply{row("b", 2), row("a", 1)}},
		{Initialized: true, Pending: []StartingSupply{row("a", -1)}},
		{Initialized: true, Pending: []StartingSupply{row("a", 4096)}},
		{Initialized: true, Pending: []StartingSupply{{Thing: "", Definition: "Steel"}}},
		{Initialized: true, Pending: []StartingSupply{{Thing: "a"}}},
	} {
		if _, _, err = ReviewStartingSupplies(domain.Unknown[[]StartingSupply](), bad); err == nil {
			t.Fatal("invalid history accepted", bad)
		}
	}
	if _, _, err = ReviewStartingSupplies(domain.Known([]StartingSupply{row("a", 1), row("a", 2)}), StartingSupplies{}); err == nil {
		t.Fatal("duplicate census accepted")
	}
}
