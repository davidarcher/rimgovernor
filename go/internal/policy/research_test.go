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

func TestUsableResearchLaboratoriesRequiresKnownFacilities(t *testing.T) {
	requirement := ResearchLabRequirement{RequiredBuilding: domain.Unknown[string]()}
	benches := []ResearchBench{{DefName: "SimpleResearchBench", Powered: true}}
	if got := UsableResearchLaboratories(requirement, benches); got != nil {
		t.Fatalf("expected nil, got %v", got)
	}
}

func TestUsableResearchLaboratoriesFiltersOnPowerBuildingAndFacilities(t *testing.T) {
	requirement := ResearchLabRequirement{
		RequiredBuilding:   domain.Known("HiTechResearchBench"),
		RequiredFacilities: domain.Known([]string{"MultiAnalyzer"}),
	}
	benches := []ResearchBench{
		{DefName: "HiTechResearchBench", Powered: false},
		{DefName: "SimpleResearchBench", Powered: true},
		{DefName: "HiTechResearchBench", Powered: true},
		{DefName: "HiTechResearchBench", Powered: true, Facilities: []ResearchBenchFacility{{DefName: "MultiAnalyzer", Active: true}}},
	}
	usable := UsableResearchLaboratories(requirement, benches)
	if len(usable) != 1 || len(usable[0].Facilities) != 1 {
		t.Fatalf("got %v", usable)
	}
}

func TestUsableResearchLaboratoriesNoBuildingRequirement(t *testing.T) {
	requirement := ResearchLabRequirement{
		RequiredBuilding:   domain.Unknown[string](),
		RequiredFacilities: domain.Known([]string{}),
	}
	benches := []ResearchBench{
		{DefName: "SimpleResearchBench", Powered: true},
		{DefName: "HiTechResearchBench", Powered: false},
	}
	usable := UsableResearchLaboratories(requirement, benches)
	if len(usable) != 1 || usable[0].DefName != "SimpleResearchBench" {
		t.Fatalf("got %v", usable)
	}
}

func researchPawn(id PawnID, priority int, disabled bool) ResearchPawn {
	return ResearchPawn{
		Pawn:        id,
		Dead:        domain.Known(false),
		Downed:      domain.Known(false),
		Drafted:     domain.Known(false),
		MentalState: domain.Known(false),
		Applies:     domain.Known(true),
		Work:        domain.Known([]WorkPriority{{Work: researchWorkType, Priority: priority, Disabled: disabled}}),
	}
}

func TestEligibleResearchersFiltersIncapacitatedAndDisabled(t *testing.T) {
	pawns := []ResearchPawn{
		researchPawn("alice", 1, false),
		researchPawn("bob", 0, false),
		researchPawn("carol", 1, true),
	}
	pawns[1].Downed = domain.Known(true)
	got := EligibleResearchers(pawns, nil)
	if len(got) != 1 || got[0] != "alice" {
		t.Fatalf("got %v", got)
	}
}

func TestEligibleResearchersExcludesZeroedOverride(t *testing.T) {
	pawns := []ResearchPawn{researchPawn("alice", 1, false)}
	overrides := []WorkOverride{{Pawn: "alice", Work: researchWorkType, Priority: 0}}
	if got := EligibleResearchers(pawns, overrides); len(got) != 0 {
		t.Fatalf("got %v", got)
	}
}

func TestEligibleResearchersRequiresApplies(t *testing.T) {
	pawn := researchPawn("alice", 1, false)
	pawn.Applies = domain.Unknown[bool]()
	if got := EligibleResearchers([]ResearchPawn{pawn}, nil); len(got) != 0 {
		t.Fatalf("got %v", got)
	}
}

func TestEligibleResearchersStableSortedOrder(t *testing.T) {
	pawns := []ResearchPawn{
		researchPawn("zed", 1, false),
		researchPawn("amy", 1, false),
	}
	got := EligibleResearchers(pawns, nil)
	if len(got) != 2 || got[0] != "amy" || got[1] != "zed" {
		t.Fatalf("got %v", got)
	}
}
