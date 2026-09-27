package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func researchProject(name ResearchProjectID, prerequisites, hidden []ResearchProjectID) ResearchProjectFacts {
	return ResearchProjectFacts{
		Name:                name,
		Hidden:              domain.Known(false),
		Prerequisites:       domain.Known(prerequisites),
		HiddenPrerequisites: domain.Known(hidden),
	}
}

func TestResearchPrerequisiteQueueOrdersAndDedupes(t *testing.T) {
	projects := map[ResearchProjectID]ResearchProjectFacts{
		"A": researchProject("A", nil, nil),
		"B": researchProject("B", []ResearchProjectID{"A"}, nil),
		"C": researchProject("C", []ResearchProjectID{"A"}, nil),
		"D": researchProject("D", []ResearchProjectID{"B", "C"}, nil),
	}
	queue, err := ResearchPrerequisiteQueue(projects, nil, []ResearchProjectID{"D"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	want := []ResearchProjectID{"A", "B", "C", "D"}
	if len(queue) != len(want) {
		t.Fatalf("got %v want %v", queue, want)
	}
	for i, name := range want {
		if queue[i] != name {
			t.Fatalf("got %v want %v", queue, want)
		}
	}
}

func TestResearchPrerequisiteQueueSkipsFinished(t *testing.T) {
	projects := map[ResearchProjectID]ResearchProjectFacts{
		"A": researchProject("A", nil, nil),
		"B": researchProject("B", []ResearchProjectID{"A"}, nil),
	}
	queue, err := ResearchPrerequisiteQueue(projects, []ResearchProjectID{"A"}, []ResearchProjectID{"B"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(queue) != 1 || queue[0] != "B" {
		t.Fatalf("got %v", queue)
	}
}

func TestResearchPrerequisiteQueueRejectsCycle(t *testing.T) {
	projects := map[ResearchProjectID]ResearchProjectFacts{
		"A": researchProject("A", []ResearchProjectID{"B"}, nil),
		"B": researchProject("B", []ResearchProjectID{"A"}, nil),
	}
	if _, err := ResearchPrerequisiteQueue(projects, nil, []ResearchProjectID{"A"}); err == nil {
		t.Fatalf("expected cycle error")
	}
}

func TestResearchPrerequisiteQueueRejectsUnknownProject(t *testing.T) {
	projects := map[ResearchProjectID]ResearchProjectFacts{}
	if _, err := ResearchPrerequisiteQueue(projects, nil, []ResearchProjectID{"Missing"}); err == nil {
		t.Fatalf("expected unavailable-prerequisite error")
	}
}

func TestResearchPrerequisiteQueueRejectsHiddenOrKnowledgeCategory(t *testing.T) {
	hidden := researchProject("H", nil, nil)
	hidden.Hidden = domain.Known(true)
	projects := map[ResearchProjectID]ResearchProjectFacts{"H": hidden}
	if _, err := ResearchPrerequisiteQueue(projects, nil, []ResearchProjectID{"H"}); err == nil {
		t.Fatalf("expected hidden-project error")
	}

	anomaly := researchProject("Anomaly", nil, nil)
	anomaly.KnowledgeCategory = "Sight"
	projects = map[ResearchProjectID]ResearchProjectFacts{"Anomaly": anomaly}
	if _, err := ResearchPrerequisiteQueue(projects, nil, []ResearchProjectID{"Anomaly"}); err == nil {
		t.Fatalf("expected knowledge-category error")
	}
}

func TestResearchPrerequisiteQueueRejectsIncompletePrerequisites(t *testing.T) {
	incomplete := ResearchProjectFacts{Name: "I", Hidden: domain.Known(false)}
	projects := map[ResearchProjectID]ResearchProjectFacts{"I": incomplete}
	if _, err := ResearchPrerequisiteQueue(projects, nil, []ResearchProjectID{"I"}); err == nil {
		t.Fatalf("expected incomplete-prerequisites error")
	}
}

func TestResearchPrerequisiteQueueCapsAtMax(t *testing.T) {
	projects := map[ResearchProjectID]ResearchProjectFacts{}
	var targets []ResearchProjectID
	for i := 0; i < ResearchQueueMax+4; i++ {
		name := ResearchProjectID(rune('A' + i))
		projects[name] = researchProject(name, nil, nil)
		targets = append(targets, name)
	}
	queue, err := ResearchPrerequisiteQueue(projects, nil, targets)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(queue) != ResearchQueueMax {
		t.Fatalf("got %d entries, want %d", len(queue), ResearchQueueMax)
	}
}

func TestResearchBenchNeededIsTheOnlyLock(t *testing.T) {
	if ResearchBenchNeeded(ResearchProjectFacts{}) {
		t.Fatal("a project that can start needs no bench")
	}
	if !ResearchBenchNeeded(ResearchProjectFacts{RequiredBuilding: "SimpleResearchBench", LockReasons: []string{ResearchLockBench}}) {
		t.Fatal("the bench lock alone is a bench need")
	}
	if ResearchBenchNeeded(ResearchProjectFacts{LockReasons: []string{"prerequisite:Electricity", ResearchLockBench}}) {
		t.Fatal("a prerequisite lock is owed first")
	}
}

func TestResearchTargetNeedStaysInDeficitWhileTheCurrentProjectLacksABench(t *testing.T) {
	facts := ResearchFacts{Current: "Stonecutting", Projects: []ResearchProjectID{"Stonecutting"}}
	if recovered, _ := ResearchTargetNeed("Stonecutting", false, domain.Known(facts)); recovered != domain.Known(true) {
		t.Fatal("a current target without a bench lock recovers", recovered)
	}
	facts.CurrentBenchMissing = true
	if recovered, deficit := ResearchTargetNeed("Stonecutting", false, domain.Known(facts)); recovered != domain.Known(false) || deficit != domain.Known(1.0) {
		t.Fatal("a current target nobody can research stays in deficit", recovered, deficit)
	}
}
