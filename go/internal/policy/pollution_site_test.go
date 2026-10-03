package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func cellsAt(x, z int32) []domain.Cell { return []domain.Cell{{X: x, Z: z}} }

func TestPollutionSitesPreferFarFromFieldsBedroomsLivingAndPollution(t *testing.T) {
	r := PollutionSiteRequest{
		Bounds:     Bounds{40, 40},
		Candidates: []Rectangle{{2, 2, 2, 2}, {18, 18, 2, 2}, {36, 36, 2, 2}},
		FieldCells: cellsAt(0, 0), BedroomCells: cellsAt(38, 38), LivingCells: cellsAt(19, 19), PollutedCells: cellsAt(30, 5),
	}
	got, err := PollutionSites(r)
	if err != nil {
		t.Fatal(err)
	}
	// The middle site contains a living-room cell; (36,36) is two cells from
	// the bedroom; (2,2) is the farthest from the field.
	if got[0].Site != (Rectangle{2, 2, 2, 2}) || got[1].Site != (Rectangle{36, 36, 2, 2}) || got[2].Site != (Rectangle{18, 18, 2, 2}) {
		t.Fatalf("order: %+v", got)
	}
	if got[2].ClearanceSquared != 0 || got[0].ClearanceSquared != 8 || got[1].ClearanceSquared != 2 {
		t.Fatalf("clearance: %+v", got)
	}
}

func TestPollutionSitesEachAvoidSetCounts(t *testing.T) {
	site := []Rectangle{{10, 10, 1, 1}, {20, 20, 1, 1}}
	for name, set := range map[string]func(*PollutionSiteRequest){
		"field":    func(r *PollutionSiteRequest) { r.FieldCells = cellsAt(9, 10) },
		"bedroom":  func(r *PollutionSiteRequest) { r.BedroomCells = cellsAt(9, 10) },
		"living":   func(r *PollutionSiteRequest) { r.LivingCells = cellsAt(9, 10) },
		"polluted": func(r *PollutionSiteRequest) { r.PollutedCells = cellsAt(9, 10) },
	} {
		r := PollutionSiteRequest{Bounds: Bounds{30, 30}, Candidates: site}
		set(&r)
		got, err := PollutionSites(r)
		if err != nil || got[0].Site.X != 20 {
			t.Fatalf("%s: %+v %v", name, got, err)
		}
	}
}

func TestPollutionSitesDisposalBreaksClearanceTies(t *testing.T) {
	r := PollutionSiteRequest{
		Bounds:     Bounds{30, 30},
		Candidates: []Rectangle{{5, 15, 1, 1}, {25, 15, 1, 1}},
		FieldCells: cellsAt(15, 15), // 10 cells from both candidates
		Disposal:   cellsAt(24, 15),
	}
	got, err := PollutionSites(r)
	if err != nil {
		t.Fatal(err)
	}
	if got[0].Site.X != 25 || got[0].DisposalSquared != 1 || got[1].DisposalSquared != 361 {
		t.Fatalf("disposal tiebreak: %+v", got)
	}
}

func TestPollutionSitesOrderIsDeterministicWithNoFacts(t *testing.T) {
	r := PollutionSiteRequest{Bounds: Bounds{10, 10}, Candidates: []Rectangle{{5, 5, 1, 1}, {1, 9, 1, 1}, {1, 2, 1, 1}}}
	got, err := PollutionSites(r)
	if err != nil {
		t.Fatal(err)
	}
	if got[0].ClearanceSquared != -1 || got[0].Site != (Rectangle{1, 2, 1, 1}) || got[1].Site != (Rectangle{1, 9, 1, 1}) {
		t.Fatalf("cell order: %+v", got)
	}
}

func TestPollutionSitesFailLoudly(t *testing.T) {
	for name, r := range map[string]PollutionSiteRequest{
		"bounds":    {},
		"candidate": {Bounds: Bounds{5, 5}, Candidates: []Rectangle{{4, 4, 2, 2}}},
		"avoid":     {Bounds: Bounds{5, 5}, FieldCells: cellsAt(5, 0)},
		"disposal":  {Bounds: Bounds{5, 5}, Disposal: cellsAt(0, -1)},
	} {
		if _, err := PollutionSites(r); err == nil {
			t.Fatalf("%s accepted", name)
		}
	}
}
