package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

func TestPerimeterOuterSectionsStone(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	p, _ := outerPlan(t, 40)
	sections, err := PerimeterOuterSections(p, "Wall", "Door", nil)
	if err != nil || len(sections) == 0 {
		t.Fatal("no outer sections", err)
	}
	doors := 0
	for _, s := range sections {
		if !IsOuterPerimeterTier(s.Name) || IsCorePerimeterTier(s.Name) {
			t.Fatal("outer section misnamed", s.Name)
		}
		for _, b := range s.Buildings {
			if b.Stuff() != "" || b.Definition() == PerimeterBridge {
				t.Fatal("outer ring is stone, never bridged", b.Definition(), b.Stuff())
			}
			if b.Definition() == "Door" {
				doors++
			}
		}
	}
	if doors == 0 {
		t.Fatal("no outer gate doors")
	}
	core, err := PerimeterSections(p, "Wall", "Door", PerimeterBridge, nil)
	if err != nil {
		t.Fatal(err)
	}
	for _, s := range core {
		if IsOuterPerimeterTier(s.Name) {
			t.Fatal("the core cut took the outer ring", s.Name)
		}
	}
	if !IsCorePerimeterTier("perimeter-00") || !IsCorePerimeterTier("perimeter-r2-07") || IsCorePerimeterTier(TierGeothermal) || IsCorePerimeterTier("perimeter-r1-x00") || IsCorePerimeterTier("perimeter-outer-00") {
		t.Fatal("core tier names")
	}
	if !IsOuterPerimeterTier("perimeter-r1-outer-03") {
		t.Fatal("a re-cut outer tier lost its mark")
	}
}
