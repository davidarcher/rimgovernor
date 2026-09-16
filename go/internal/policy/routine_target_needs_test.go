package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestResearchTargetNeedMeasuresNativeState(t *testing.T) {
	listed := ResearchFacts{Projects: []ResearchProjectID{"Stonecutting", "Electricity"}}
	cases := []struct {
		name      string
		target    string
		facts     domain.Fact[ResearchFacts]
		recovered domain.Fact[bool]
		deficit   domain.Fact[float64]
	}{
		{"no target", "", domain.Unknown[ResearchFacts](), domain.Known(true), domain.Known(0.0)},
		{"missing facts", "Stonecutting", domain.Unknown[ResearchFacts](), domain.Unknown[bool](), domain.Unknown[float64]()},
		{"unlisted target", "Fabrication", domain.Known(listed), domain.Unknown[bool](), domain.Unknown[float64]()},
		{"idle tab", "Stonecutting", domain.Known(listed), domain.Known(false), domain.Known(1.0)},
		{"player project respected", "Stonecutting", domain.Known(ResearchFacts{Current: "Electricity", Projects: listed.Projects}), domain.Known(true), domain.Known(0.0)},
		{"finished", "Stonecutting", domain.Known(ResearchFacts{Finished: []ResearchProjectID{"Stonecutting"}, Projects: listed.Projects}), domain.Known(true), domain.Known(0.0)},
	}
	for _, c := range cases {
		recovered, deficit := ResearchTargetNeed(c.target, c.facts)
		if recovered != c.recovered || deficit != c.deficit {
			t.Fatal(c.name, recovered, deficit)
		}
	}
}

func TestResourceTargetNeedUsesWorstCoveredTarget(t *testing.T) {
	targets := map[Resource]int64{"Steel": 100, "WoodLog": 200}
	if recovered, deficit := ResourceTargetNeed(nil, domain.Unknown[[]Amount]()); recovered != domain.Known(true) || deficit != domain.Known(0.0) {
		t.Fatal("no targets", recovered, deficit)
	}
	if recovered, deficit := ResourceTargetNeed(targets, domain.Unknown[[]Amount]()); recovered != domain.Unknown[bool]() || deficit != domain.Unknown[float64]() {
		t.Fatal("unknown stock", recovered, deficit)
	}
	stock := domain.Known([]Amount{{Resource: "Steel", Count: 75}, {Resource: "WoodLog", Count: 50}})
	if recovered, deficit := ResourceTargetNeed(targets, stock); recovered != domain.Known(false) || deficit != domain.Known(0.75) {
		t.Fatal("worst target", recovered, deficit)
	}
	// A configured resource absent from the census is fully unstocked.
	if _, deficit := ResourceTargetNeed(targets, domain.Known([]Amount{{Resource: "WoodLog", Count: 200}})); deficit != domain.Known(1.0) {
		t.Fatal("absent resource", deficit)
	}
	if recovered, deficit := ResourceTargetNeed(targets, domain.Known([]Amount{{Resource: "Steel", Count: 100}, {Resource: "WoodLog", Count: 250}})); recovered != domain.Known(true) || deficit != domain.Known(0.0) {
		t.Fatal("recovered", recovered, deficit)
	}
	if recovered, _ := ResourceTargetNeed(map[Resource]int64{"Steel": -1}, stock); recovered != domain.Unknown[bool]() {
		t.Fatal("invalid target must not recover", recovered)
	}
}

// Configured research and resource targets must be able to win a development
// slot: a measured deficit ranks them alongside comfort and expansion instead
// of leaving them permanently deficit_unknown.
func TestConfiguredTargetsRankForDevelopment(t *testing.T) {
	p := DefaultRoutinePolicy()
	p.ResearchTarget = "Stonecutting"
	p.ResourceTargets = map[Resource]int64{"Steel": 100}
	p.ResourceReserves = map[Resource]int64{"WoodLog": 50}
	f := stableRoutine()
	f.Research = domain.Known(ResearchFacts{Projects: []ResearchProjectID{"Stonecutting"}})
	f.Resources = domain.Known([]Amount{{Resource: "Steel", Count: 40}})
	r, err := DetectRoutine(f, RoutineLatches{}, p)
	if err != nil {
		t.Fatal(err)
	}
	if hasNeed(r, ProductionPolicy) || !DevelopmentExempt(ProductionPolicy) {
		t.Fatal("configuration push must not hold a development slot")
	}
	assessed := map[GoalID]domain.NeedState{}
	for _, a := range r.Assessments {
		assessed[a.ID] = a.Need
	}
	if assessed[ProductionPolicy] != domain.NeedDeficit || assessed[EnsureResearch] != domain.NeedDeficit || assessed[MaintainResource] != domain.NeedDeficit {
		t.Fatal(assessed)
	}
	state, err := RankDevelopment(DevelopmentRequest{Snapshot: domain.GenerationSnapshot{Colony: "colony", Map: 1, Load: "load", Plan: "plan", Direction: 1}, Tick: 100, Workers: domain.Known(3), Limit: 2, Goals: r.Goals})
	if err != nil {
		t.Fatal(err)
	}
	selected := map[GoalID]DevelopmentRow{}
	for _, row := range state.Rows {
		selected[row.Goal] = row
	}
	if !selected[EnsureResearch].Selected || selected[EnsureResearch].Score != 100 {
		t.Fatal(selected[EnsureResearch])
	}
	if !selected[MaintainResource].Selected || selected[MaintainResource].Score != 60 {
		t.Fatal(selected[MaintainResource])
	}
	// Missing native facts keep a configured target unknown, never recovered.
	f.Research, f.Resources = domain.Unknown[ResearchFacts](), domain.Unknown[[]Amount]()
	r, err = DetectRoutine(f, RoutineLatches{}, p)
	if err != nil {
		t.Fatal(err)
	}
	for _, a := range r.Assessments {
		if (a.ID == EnsureResearch || a.ID == MaintainResource) && a.Need != domain.NeedUnknown {
			t.Fatal(a)
		}
	}
}
