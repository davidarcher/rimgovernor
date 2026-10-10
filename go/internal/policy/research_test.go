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

func TestResearchPrerequisiteQueueRejectsHiddenProjects(t *testing.T) {
	hidden := researchProject("H", nil, nil)
	hidden.Hidden = domain.Known(true)
	projects := map[ResearchProjectID]ResearchProjectFacts{"H": hidden}
	if _, err := ResearchPrerequisiteQueue(projects, nil, []ResearchProjectID{"H"}); err == nil {
		t.Fatalf("expected hidden-project error")
	}
}

// A knowledge-category project is an ordinary node of the graph: the
// queue orders it after its ordinary and knowledge prerequisites alike.
func TestResearchPrerequisiteQueueAcceptsKnowledgeProjects(t *testing.T) {
	extraction := researchProject("BioferriteExtraction", nil, nil)
	extraction.KnowledgeCategory = "Basic"
	shaping := researchProject("BioferriteShaping", []ResearchProjectID{"BioferriteExtraction", "Electricity"}, nil)
	shaping.KnowledgeCategory = "Basic"
	projects := map[ResearchProjectID]ResearchProjectFacts{
		"Electricity":          researchProject("Electricity", nil, nil),
		"BioferriteExtraction": extraction,
		"BioferriteShaping":    shaping,
	}
	queue, err := ResearchPrerequisiteQueue(projects, nil, []ResearchProjectID{"BioferriteShaping"})
	want := []ResearchProjectID{"BioferriteExtraction", "Electricity", "BioferriteShaping"}
	if err != nil || len(queue) != len(want) {
		t.Fatal(queue, err)
	}
	for i := range want {
		if queue[i] != want[i] {
			t.Fatal(queue, want)
		}
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

func TestDefaultLadderQueuesChargedShotThenBeamWeapons(t *testing.T) {
	ladder := DefaultResearchLadder()
	if n := len(ladder); ladder[n-2] != "ChargedShot" || ladder[n-1] != "BeamWeapons" {
		t.Fatal("ladder tail", ladder)
	}
	p := RoundsPolicy{ResearchLadder: ladder}
	finished := []ResearchProjectID{}
	for _, rung := range ladder[:len(ladder)-2] {
		finished = append(finished, ResearchProjectID(rung))
	}
	listed := append(append([]ResearchProjectID{}, finished...), "ChargedShot")
	// Without Odyssey the census does not list BeamWeapons: the ladder
	// ends at ChargedShot, then has no target and no stall on an unlisted rung.
	facts := domain.Known(ResearchFacts{Projects: listed, Finished: finished})
	if target, _ := ResearchConcern(p, nil, facts); target != "ChargedShot" {
		t.Fatal("charged shot target", target)
	}
	finished = append(finished, "ChargedShot")
	facts = domain.Known(ResearchFacts{Projects: listed, Finished: finished})
	if target, _ := ResearchConcern(p, nil, facts); target != "" {
		t.Fatal("beam weapons walked without Odyssey", target)
	}
	// With Odyssey it is listed and walked next, its prerequisite chain queued.
	listed = append(listed, "BeamWeapons")
	facts = domain.Known(ResearchFacts{Projects: listed, Finished: finished})
	if target, _ := ResearchConcern(p, nil, facts); target != "BeamWeapons" {
		t.Fatal("beam weapons target", target)
	}
	projects := map[ResearchProjectID]ResearchProjectFacts{
		"Gunsmithing": researchProject("Gunsmithing", nil, nil),
		"ChargedShot": researchProject("ChargedShot", []ResearchProjectID{"Gunsmithing"}, nil),
		"BeamWeapons": researchProject("BeamWeapons", []ResearchProjectID{"ChargedShot"}, nil),
	}
	queue, err := ResearchPrerequisiteQueue(projects, nil, []ResearchProjectID{"BeamWeapons"})
	if err != nil || len(queue) != 3 || queue[0] != "Gunsmithing" || queue[2] != "BeamWeapons" {
		t.Fatal(queue, err)
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
