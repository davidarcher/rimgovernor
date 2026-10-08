package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"testing"
)

func TestRecordedCatalogStartingDesign(t *testing.T) {
	catalog := fullCatalog(t)
	choice, err := catalog.StartingIdeoligion("Crashlanded")
	if err != nil {
		opts, _ := catalog.IdeoligionOptions("PlayerColony")
		for _, m := range opts.Memes {
			if m.Allowed {
				t.Logf("meme %+v", m)
			}
		}
		for _, p := range opts.Precepts {
			if p.Default || p.Supported {
				t.Logf("precept %+v", p)
			}
		}
		t.Fatal(err)
	}
	options, err := catalog.IdeoligionOptions("PlayerColony")
	if err != nil {
		t.Fatal(err)
	}
	if _, known := policy.ScoreIdeoligion(options, choice.Design).Value(); !known {
		t.Fatal("selected design has unvalued required effects")
	}
	if choice.Candidates > 100000 {
		t.Fatalf("unbounded work: %d", choice.Candidates)
	}
	t.Logf("selected %+v score %+v visits %d", choice.Design, choice.Score, choice.Candidates)
}
