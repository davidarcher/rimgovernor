package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestDeepDrillingResearchRequiresMetalDeficit(t *testing.T) {
	for _, resource := range []Resource{"Steel", "Plasteel", "ComponentIndustrial"} {
		for _, deficit := range []domain.Fact[bool]{domain.Unknown[bool](), domain.Known(false), domain.Known(true)} {
			needs := DeepDrillingResearch(nil, []ResourceRunway{{Resource: resource, Deficit: deficit}})
			want := deficit == domain.Known(true) && resource != "ComponentIndustrial"
			if (len(needs) == 2) != want {
				t.Fatal(resource, deficit, needs)
			}
			if !want {
				continue
			}
			facts := ResearchFacts{Projects: []ResearchProjectID{"DeepDrilling", "GroundPenetratingScanner"}}
			if target, _ := ResearchGoal(DefaultRoutinePolicy(), needs, domain.Known(facts)); target != "DeepDrilling" {
				t.Fatal(target)
			}
			facts.Finished = []ResearchProjectID{"DeepDrilling"}
			if target, _ := ResearchGoal(DefaultRoutinePolicy(), needs, domain.Known(facts)); target != "GroundPenetratingScanner" {
				t.Fatal(target)
			}
		}
	}
}

func TestMetalRunwayRaisesResearchNeed(t *testing.T) {
	f := stableRoutine()
	f.Research = domain.Known(ResearchFacts{Projects: []ResearchProjectID{"DeepDrilling", "GroundPenetratingScanner"}})
	f.ResourceRunways = []ResourceRunway{{Resource: "Steel", Deficit: domain.Known(true)}}
	if got := assessment(t, needs(t, f, RoutineLatches{}), EnsureResearch); got != domain.NeedDeficit {
		t.Fatal(got)
	}
	f.ResourceRunways = nil
	if got := assessment(t, needs(t, f, RoutineLatches{}), EnsureResearch); got != domain.NeedRecovered {
		t.Fatal(got)
	}
}
