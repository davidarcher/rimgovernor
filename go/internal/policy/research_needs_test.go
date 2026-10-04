package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func knowledgeProject(name, category string, cost float64, locks ...string) ResearchProjectFacts {
	return ResearchProjectFacts{Name: ResearchProjectID(name), Hidden: domain.Known(false), KnowledgeCategory: category, Cost: cost, Census: true, LockReasons: locks}
}

// The cheapest startable project of an empty slot's own category funds
// first; locked, hidden, finished and other-category projects never do, so
// Basic knowledge cannot be spent on an Advanced project and the other way
// round (the overflow runs Advanced into Basic only, in the game).
func TestKnowledgePickFundsTheCheapestStartableProjectOfTheSlotsCategory(t *testing.T) {
	hidden := knowledgeProject("HiddenCheap", "Basic", 1)
	hidden.Hidden = domain.Known(true)
	unread := knowledgeProject("Unread", "Basic", 2)
	unread.Census = false
	projects := map[string]ResearchProjectFacts{
		"Dear":       knowledgeProject("Dear", "Basic", 20),
		"Cheap":      knowledgeProject("Cheap", "Basic", 5),
		"Tied":       knowledgeProject("Tied", "Basic", 5),
		"Locked":     knowledgeProject("Locked", "Basic", 1, "prerequisite:Dear"),
		"Done":       knowledgeProject("Done", "Basic", 1),
		"HiddenCost": hidden,
		"Unread":     unread,
		"Gen":        knowledgeProject("Gen", "Advanced", 40),
		"Ordinary":   {Name: "Ordinary", Hidden: domain.Known(false), Census: true, Cost: 1},
	}
	slots := []KnowledgeSlot{{Category: "Basic"}, {Category: "Advanced", Current: "Gen"}}
	if got := KnowledgePick(projects, []string{"Done"}, slots); got != "Cheap" {
		t.Fatal("basic slot, cheapest then name", got)
	}
	slots[0].Current = "Cheap"
	slots[1].Current = ""
	if got := KnowledgePick(projects, nil, slots); got != "Gen" {
		t.Fatal("only the advanced slot is empty", got)
	}
	slots[1].Current = "Gen"
	if got := KnowledgePick(projects, nil, slots); got != "" {
		t.Fatal("every slot holds a project", got)
	}
}

func TestKnowledgePickNeverFundsAnAdvancedProjectFromTheBasicSlot(t *testing.T) {
	projects := map[string]ResearchProjectFacts{"Gen": knowledgeProject("Gen", "Advanced", 40)}
	if got := KnowledgePick(projects, nil, []KnowledgeSlot{{Category: "Basic"}}); got != "" {
		t.Fatal("an empty basic slot has no advanced project to fund", got)
	}
	if got := KnowledgePick(projects, nil, nil); got != "" {
		t.Fatal("anomaly inactive means no slots", got)
	}
}

// An empty knowledge slot with a project to fund holds EnsureResearch in
// deficit even with every ordinary rung finished, and a disabled goal (no
// target, empty ladder) funds none.
func TestReviewCountsAnEmptyKnowledgeSlotAsAResearchDeficit(t *testing.T) {
	f := stableRoutine()
	census := ResearchFacts{Projects: []ResearchProjectID{"Stonecutting", "Electricity"}, Finished: []ResearchProjectID{"Stonecutting", "Electricity"}}
	f.Research = domain.Known(census)
	if r := needs(t, f, RoutineLatches{}); assessment(t, r, EnsureResearch) != domain.FindingMet {
		t.Fatal("no slot owed", r.Assessments)
	}
	census.KnowledgePick = "BioferriteExtraction"
	f.Research = domain.Known(census)
	r := needs(t, f, RoutineLatches{})
	if assessment(t, r, EnsureResearch) != domain.FindingUnmet || !hasNeed(r, EnsureResearch) {
		t.Fatal("an empty knowledge slot is a spending need", r.Assessments)
	}
	for _, g := range r.Goals {
		if g.ID == EnsureResearch && g.Deficit != domain.Known(1.0) {
			t.Fatal("knowledge deficit", g)
		}
	}
}
