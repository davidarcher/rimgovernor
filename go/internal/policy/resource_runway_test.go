package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestSurfaceOreOnlySafeMineables(t *testing.T) {
	sources := []ResourceSource{{ThingID: "a", Method: ResourceSourceMine, Safety: "open_surface", Yield: 20, Designated: true}, {ThingID: "b", Method: ResourceSourceMine, Safety: "roofed", Yield: 100}, {ThingID: "c", Method: "haul", Yield: 40}, {ThingID: "d", Method: ResourceSourceMine, Safety: MineSafetySupportedRoof, Yield: 30}}
	if n, k := SurfaceOre(sources).Value(); !k || n != 50 {
		t.Fatal(n, k)
	}
	if _, k := SurfaceOre(append(sources, sources[0])).Value(); k {
		t.Fatal("duplicate ore")
	}
}

func TestRunwaySurfacesMaintainResourceDeficit(t *testing.T) {
	p := DefaultRoundsPolicy()
	f := RoundsFacts{Resources: domain.Known([]Amount{{Resource: "Steel", Count: 100}}), ResourceRunways: []ResourceRunway{{Resource: "Steel", DaysLeft: domain.Known(2.0), Deficit: domain.Known(true)}}}
	r, err := InspectRounds(f, RoundsLatches{}, p)
	if err != nil {
		t.Fatal(err)
	}
	for _, a := range r.Assessments {
		if a.ID == MaintainResource {
			if a.Finding != domain.FindingUnmet {
				t.Fatal(a)
			}
			return
		}
	}
	t.Fatal("missing MaintainResource")
}

func TestUnknownRunwayCannotRecoverMaintenance(t *testing.T) {
	f := RoundsFacts{Resources: domain.Known([]Amount{}), ResourceRunways: []ResourceRunway{{Resource: "Steel", WindowDays: 2}}}
	r, err := InspectRounds(f, RoundsLatches{}, DefaultRoundsPolicy())
	if err != nil {
		t.Fatal(err)
	}
	for _, a := range r.Assessments {
		if a.ID == MaintainResource {
			if a.Finding != domain.FindingUnclear {
				t.Fatal(a)
			}
			return
		}
	}
	t.Fatal("missing resource assessment")
}
