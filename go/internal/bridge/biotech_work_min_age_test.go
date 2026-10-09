package bridge

import (
	"testing"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// TestBiotechWorkMinAges: the race's minimums come from the catalog;
// a race it lacks fails loudly.
func TestBiotechWorkMinAges(t *testing.T) {
	cat := &BiotechCatalog{races: map[string]*d.RaceProperties{"Human": {
		LifeStageWorkSettings: []*d.Opt_LifeStageWorkSettings{{Value: &d.LifeStageWorkSettings{WorkType: "Hauling", MinAge: 3}}},
	}}}
	got, err := cat.WorkMinAges("Human")
	if err != nil || len(got) != 1 || got["Hauling"] != 3 {
		t.Fatalf("ages = %v, %v", got, err)
	}
	if _, err := cat.WorkMinAges("Alien"); err == nil {
		t.Fatal("unknown child race accepted")
	}
	var none *BiotechCatalog
	if _, err := none.WorkMinAges("Human"); err == nil {
		t.Fatal("missing catalog accepted")
	}
}
