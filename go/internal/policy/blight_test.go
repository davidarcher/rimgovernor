package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestRemoveBlightOpensOnCensusAndSettlesOnEmpty(t *testing.T) {
	t.Parallel()
	f := stableRounds()
	f.Blight = domain.Unknown[[]BlightedPlant]()
	r := needs(t, f, RoundsLatches{})
	if assessment(t, r, RemoveBlight) != domain.FindingUnclear || !hasNeed(r, RemoveBlight) {
		t.Fatal("unknown census must not count as recovered", r)
	}
	f.Blight = domain.Known([]BlightedPlant{{ID: "Plant_Rice1", Designated: true}})
	f.AvailableMethods = domain.Known([]ConcernID{})
	r = needs(t, f, r.Latches)
	if !hasNeed(r, RemoveBlight) {
		t.Fatal("a designated but standing plant keeps the goal open", r)
	}
	for _, g := range r.Concerns {
		if g.ID == RemoveBlight {
			if d, known := g.Deficit.Value(); !known || d != 1.0 || !g.MethodUnavailable {
				t.Fatal("census deficit must be full and ungated methods unavailable", g)
			}
		}
	}
	f.AvailableMethods = domain.Known([]ConcernID{RemoveBlight})
	r = needs(t, f, r.Latches)
	for _, g := range r.Concerns {
		if g.ID == RemoveBlight && g.MethodUnavailable {
			t.Fatal("configured method must be available", g)
		}
	}
	f.Blight = domain.Known([]BlightedPlant{})
	r = needs(t, f, r.Latches)
	if hasNeed(r, RemoveBlight) || assessment(t, r, RemoveBlight) != domain.FindingMet {
		t.Fatal("empty census settles the goal", r)
	}
}

func TestSelectBlightCutsSkipsDesignatedAndClaimed(t *testing.T) {
	t.Parallel()
	plants := []BlightedPlant{
		{ID: "c", Eligible: true}, {ID: "a", Eligible: true, Designated: true}, {ID: "b", Eligible: true}, {ID: "d", Eligible: true}, {ID: "e"},
	}
	got := SelectBlightCuts(plants, map[string]bool{"d": true}, 8)
	if len(got) != 2 || got[0].ID != "b" || got[1].ID != "c" {
		t.Fatal(got)
	}
	if got := SelectBlightCuts(plants, nil, 1); len(got) != 1 || got[0].ID != "b" {
		t.Fatal(got)
	}
}
