package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestRoundsDevelopmentNativeFractionsAndWorkers(t *testing.T) {
	p := DefaultRoundsPolicy()
	f := RoundsFacts{Colonists: domain.Known(int64(3)), Armed: domain.Known(int64(1)), Wood: domain.Known(int64(175))}
	for _, id := range []ConcernID{EnsureBasicDefense} {
		v, k := RoundsDeficit(id, f, p).Value()
		if !k || v != .5 {
			t.Fatal(id, v, k)
		}
	}
	f.Armed = domain.Unknown[int64]()
	if _, k := RoundsDeficit(EnsureBasicDefense, f, p).Value(); k {
		t.Fatal("unknown equipment became a deficit")
	}
	pawns := []WorkPawn{{Available: domain.Known(true), Applies: domain.Known(true)}, {Available: domain.Known(false)}}
	if v, k := RoundsWorkers(pawns).Value(); !k || v != 1 {
		t.Fatal(v, k)
	}
	pawns = append(pawns, WorkPawn{Applies: domain.Known(true)})
	if _, k := RoundsWorkers(pawns).Value(); k {
		t.Fatal("missing availability increased capacity")
	}
}

func TestRoundsDeficitDefensiveLayoutFollowsOptIn(t *testing.T) {
	if _, known := RoundsDeficit(EnsureDefensiveLayout, RoundsFacts{}, RoundsPolicy{}).Value(); known {
		t.Fatal("opted-out layout must rank deficit_unknown")
	}
	if v, known := RoundsDeficit(EnsureDefensiveLayout, RoundsFacts{}, RoundsPolicy{DefensiveLayout: true}).Value(); !known || v != 1 {
		t.Fatalf("opted-in layout deficit = %v,%v; want 1,true", v, known)
	}
	// A standing layout ranks by age alone so an active repair or power
	// deficit outranks it; an unknown or fallen record keeps the full deficit.
	standing := RoundsFacts{DefensiveLayoutStanding: domain.Known(true)}
	if v, known := RoundsDeficit(EnsureDefensiveLayout, standing, RoundsPolicy{DefensiveLayout: true}).Value(); !known || v != 0 {
		t.Fatalf("standing layout deficit = %v,%v; want 0,true", v, known)
	}
	fallen := RoundsFacts{DefensiveLayoutStanding: domain.Known(false)}
	if v, known := RoundsDeficit(EnsureDefensiveLayout, fallen, RoundsPolicy{DefensiveLayout: true}).Value(); !known || v != 1 {
		t.Fatalf("fallen layout deficit = %v,%v; want 1,true", v, known)
	}
	if _, known := RoundsDeficit(EnsureDefensiveLayout, standing, RoundsPolicy{}).Value(); known {
		t.Fatal("opted-out layout must rank deficit_unknown even when standing")
	}
}

func TestResearchGoalTargetDerivesFromRecordedNeeds(t *testing.T) {
	facts := domain.Known(ResearchFacts{Projects: []ResearchProjectID{"Electricity", "Smithing"}, Finished: []ResearchProjectID{"Electricity"}})
	if got := ResearchConcernTarget("Fabrication", []string{"Smithing"}, facts); got != "Fabrication" {
		t.Fatal("configured target must win", got)
	}
	if got := ResearchConcernTarget("", nil, facts); got != "" {
		t.Fatal(got)
	}
	if got := ResearchConcernTarget("", []string{"Electricity", "Smithing"}, facts); got != "Smithing" {
		t.Fatal("finished project chosen", got)
	}
	if got := ResearchConcernTarget("", []string{"Electricity", "Unlisted"}, facts); got != "" {
		t.Fatal("unlisted project chosen", got)
	}
	if got := ResearchConcernTarget("", []string{"Smithing"}, domain.Unknown[ResearchFacts]()); got != "Smithing" {
		t.Fatal("unknown facts must keep the need", got)
	}
	recovered, deficit := ResearchTargetNeed(ResearchConcernTarget("", []string{"Smithing"}, facts), true, facts)
	if v, _ := recovered.Value(); v {
		t.Fatal(recovered, deficit)
	}
	// A derived target stays a deficit while it is current and unfinished:
	// the ladder waits on it, and the clock ticks come from the research
	// planner, not from a recovered goal.
	current := domain.Known(ResearchFacts{Projects: []ResearchProjectID{"Electricity", "Smithing"}, Current: "Smithing"})
	if recovered, _ = ResearchTargetNeed("Smithing", true, current); recovered != domain.Known(false) {
		t.Fatal("derived target recovered while current", recovered)
	}
	if recovered, _ = ResearchTargetNeed("Smithing", false, current); recovered != domain.Known(true) {
		t.Fatal("configured target must respect the current project", recovered)
	}
}
