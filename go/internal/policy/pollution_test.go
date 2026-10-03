package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func yes(v bool) domain.Fact[bool] { return domain.Known(v) }

func TestManagePollutionOnlyExistsWithBiotechFacts(t *testing.T) {
	t.Parallel()
	f := stableRoutine()
	f.Pollution = domain.Unknown[PollutionFacts]()
	r := needs(t, f, RoutineLatches{})
	for _, a := range r.All() {
		if a.ID == ManagePollution {
			t.Fatal("no Biotech fact, no assessment", a)
		}
	}
	f.Pollution = domain.Known(PollutionFacts{
		Wastepacks:     []Wastepack{{ID: "Wastepack1", Frozen: yes(false), InAtomizer: yes(false), Forbidden: yes(false)}},
		UncoveredCells: domain.Known(uint32(0)),
	})
	f.AvailableMethods = domain.Known([]GoalID{ManagePollution})
	r = needs(t, f, r.Latches)
	if !hasNeed(r, ManagePollution) {
		t.Fatal("an exposed wastepack opens the goal", r)
	}
	f.Pollution = domain.Known(PollutionFacts{
		Wastepacks:     []Wastepack{{ID: "Wastepack1", Frozen: yes(true), InAtomizer: yes(false), Forbidden: yes(false)}},
		UncoveredCells: domain.Known(uint32(0)),
	})
	r = needs(t, f, r.Latches)
	if hasNeed(r, ManagePollution) || assessment(t, r, ManagePollution) != domain.NeedRecovered {
		t.Fatal("a frozen pack and a covered map settle the goal", r)
	}
}

func TestPollutionDeficit(t *testing.T) {
	t.Parallel()
	pack := func(frozen, atomized, forbidden domain.Fact[bool]) Wastepack {
		return Wastepack{ID: "w", Frozen: frozen, InAtomizer: atomized, Forbidden: forbidden}
	}
	covered := domain.Known(uint32(0))
	for _, tc := range []struct {
		name  string
		facts PollutionFacts
		want  domain.Fact[bool]
	}{
		{"frozen", PollutionFacts{Wastepacks: []Wastepack{pack(yes(true), yes(false), yes(false))}, UncoveredCells: covered}, yes(false)},
		{"atomized with frozen unknown", PollutionFacts{Wastepacks: []Wastepack{pack(domain.Unknown[bool](), yes(true), yes(false))}, UncoveredCells: covered}, yes(false)},
		{"exposed", PollutionFacts{Wastepacks: []Wastepack{pack(yes(false), yes(false), yes(false))}, UncoveredCells: covered}, yes(true)},
		{"forbidden but frozen", PollutionFacts{Wastepacks: []Wastepack{pack(yes(true), yes(false), yes(true))}, UncoveredCells: covered}, yes(true)},
		{"verdict unknown", PollutionFacts{Wastepacks: []Wastepack{pack(yes(false), domain.Unknown[bool](), yes(false))}, UncoveredCells: covered}, domain.Unknown[bool]()},
		{"uncovered cells", PollutionFacts{UncoveredCells: domain.Known(uint32(3))}, yes(true)},
		{"uncovered unknown", PollutionFacts{}, domain.Unknown[bool]()},
		{"no packs, covered", PollutionFacts{UncoveredCells: covered}, yes(false)},
	} {
		if got := PollutionDeficit(domain.Known(tc.facts)); got != tc.want {
			t.Errorf("%s: got %v want %v", tc.name, got, tc.want)
		}
	}
	if got := PollutionDeficit(domain.Unknown[PollutionFacts]()); got != domain.Unknown[bool]() {
		t.Error("unknown section stays unknown", got)
	}
}

func TestSelectPollutionWorkAllowsBeforeHaulAndSkipsClaimed(t *testing.T) {
	t.Parallel()
	exposed := func(id string) Wastepack {
		return Wastepack{ID: id, Frozen: yes(false), InAtomizer: yes(false), Forbidden: yes(false)}
	}
	held := exposed("b")
	held.Forbidden = yes(true)
	facts := PollutionFacts{
		Wastepacks:     []Wastepack{exposed("d"), held, exposed("c"), exposed("a"), {ID: "u", Frozen: domain.Unknown[bool](), InAtomizer: yes(false), Forbidden: yes(false)}},
		UncoveredCells: domain.Known(uint32(2)),
	}
	polluted := []domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 1}}
	work := SelectPollutionWork(facts, map[string]bool{"c": true}, polluted)
	if len(work.Allow) != 1 || work.Allow[0].ID != "b" || len(work.Haul) != 2 || work.Haul[0].ID != "a" || work.Haul[1].ID != "d" || len(work.Area) != 2 {
		t.Fatal(work)
	}
	facts.UncoveredCells = domain.Known(uint32(0))
	if work := SelectPollutionWork(facts, nil, polluted); len(work.Area) != 0 {
		t.Fatal("no area edit while the game counts none uncovered", work)
	}
}

func TestPollutedWindowCellsKeepsKnownPollutedOnly(t *testing.T) {
	t.Parallel()
	cells := []SiteCell{
		{Cell: domain.Cell{X: 5, Z: 1}, Polluted: yes(true)},
		{Cell: domain.Cell{X: 1, Z: 1}, Polluted: yes(true)},
		{Cell: domain.Cell{X: 2, Z: 2}, Polluted: yes(false)},
		{Cell: domain.Cell{X: 3, Z: 3}},
	}
	got := PollutedWindowCells(cells)
	if len(got) != 2 || got[0] != (domain.Cell{X: 1, Z: 1}) || got[1] != (domain.Cell{X: 5, Z: 1}) {
		t.Fatal(got)
	}
}
