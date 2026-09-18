package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestRoutineDevelopmentNativeFractionsAndWorkers(t *testing.T) {
	p := DefaultRoutinePolicy()
	f := RoutineFacts{Colonists: domain.Known(int64(3)), Armed: domain.Known(int64(1)), Wood: domain.Known(int64(175))}
	for _, id := range []GoalID{MaintainWood, EnsureBasicDefense} {
		v, k := RoutineDevelopmentDeficit(id, f, p).Value()
		if !k || v != .5 {
			t.Fatal(id, v, k)
		}
	}
	f.Armed = domain.Unknown[int64]()
	if _, k := RoutineDevelopmentDeficit(EnsureBasicDefense, f, p).Value(); k {
		t.Fatal("unknown equipment became a deficit")
	}
	pawns := []WorkPawn{{Available: domain.Known(true), Applies: domain.Known(true)}, {Available: domain.Known(false)}}
	if v, k := RoutineWorkers(pawns).Value(); !k || v != 1 {
		t.Fatal(v, k)
	}
	pawns = append(pawns, WorkPawn{Applies: domain.Known(true)})
	if _, k := RoutineWorkers(pawns).Value(); k {
		t.Fatal("missing availability increased capacity")
	}
}

func TestRoutineDevelopmentDeficitDefensiveLayoutFollowsOptIn(t *testing.T) {
	if _, known := RoutineDevelopmentDeficit(EnsureDefensiveLayout, RoutineFacts{}, RoutinePolicy{}).Value(); known {
		t.Fatal("opted-out layout must rank deficit_unknown")
	}
	if v, known := RoutineDevelopmentDeficit(EnsureDefensiveLayout, RoutineFacts{}, RoutinePolicy{DefensiveLayout: true}).Value(); !known || v != 1 {
		t.Fatalf("opted-in layout deficit = %v,%v; want 1,true", v, known)
	}
	// A standing layout ranks by age alone so an active repair or power
	// deficit outranks it; an unknown or fallen record keeps the full deficit.
	standing := RoutineFacts{DefensiveLayoutStanding: domain.Known(true)}
	if v, known := RoutineDevelopmentDeficit(EnsureDefensiveLayout, standing, RoutinePolicy{DefensiveLayout: true}).Value(); !known || v != 0 {
		t.Fatalf("standing layout deficit = %v,%v; want 0,true", v, known)
	}
	fallen := RoutineFacts{DefensiveLayoutStanding: domain.Known(false)}
	if v, known := RoutineDevelopmentDeficit(EnsureDefensiveLayout, fallen, RoutinePolicy{DefensiveLayout: true}).Value(); !known || v != 1 {
		t.Fatalf("fallen layout deficit = %v,%v; want 1,true", v, known)
	}
	if _, known := RoutineDevelopmentDeficit(EnsureDefensiveLayout, standing, RoutinePolicy{}).Value(); known {
		t.Fatal("opted-out layout must rank deficit_unknown even when standing")
	}
}

func TestResearchGoalTargetDerivesFromRecordedNeeds(t *testing.T) {
	facts := domain.Known(ResearchFacts{Projects: []ResearchProjectID{"Electricity", "Smithing"}, Finished: []ResearchProjectID{"Electricity"}})
	if got := ResearchGoalTarget("Fabrication", []string{"Smithing"}, facts); got != "Fabrication" {
		t.Fatal("configured target must win", got)
	}
	if got := ResearchGoalTarget("", nil, facts); got != "" {
		t.Fatal(got)
	}
	if got := ResearchGoalTarget("", []string{"Electricity", "Smithing"}, facts); got != "Smithing" {
		t.Fatal("finished project chosen", got)
	}
	if got := ResearchGoalTarget("", []string{"Electricity", "Unlisted"}, facts); got != "" {
		t.Fatal("unlisted project chosen", got)
	}
	if got := ResearchGoalTarget("", []string{"Smithing"}, domain.Unknown[ResearchFacts]()); got != "Smithing" {
		t.Fatal("unknown facts must keep the need", got)
	}
	recovered, deficit := ResearchTargetNeed(ResearchGoalTarget("", []string{"Smithing"}, facts), true, facts)
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
