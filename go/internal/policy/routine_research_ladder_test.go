package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The default roadmap: with no operator target and no workshop need the
// review walks RoutinePolicy.ResearchLadder in order, skipping finished and
// unlisted rungs, and only under a known census (issue #230).
func TestResearchGoalWalksTheLadder(t *testing.T) {
	p := DefaultRoutinePolicy()
	if len(p.ResearchLadder) == 0 || p.ResearchLadder[0] != "Stonecutting" || p.ResearchLadder[1] != "Electricity" {
		t.Fatal("default ladder", p.ResearchLadder)
	}
	if err := p.Validate(); err != nil {
		t.Fatal(err)
	}
	census := domain.Known(ResearchFacts{Projects: []ResearchProjectID{"Stonecutting", "Electricity", "Batteries", "Smithing"}, Finished: []ResearchProjectID{"Stonecutting"}})
	if target, derived := ResearchGoal(p, nil, census); target != "Electricity" || derived {
		t.Fatal("first unfinished rung", target, derived)
	}
	if target, _ := ResearchGoal(p, nil, domain.Unknown[ResearchFacts]()); target != "" {
		t.Fatal("a ladder rung needs a known census", target)
	}
	if target, derived := ResearchGoal(p, []string{"Smithing"}, census); target != "Smithing" || !derived {
		t.Fatal("a workshop need precedes the ladder", target, derived)
	}
	if target, derived := ResearchGoal(p, []string{"Stonecutting"}, census); target != "Electricity" || derived {
		t.Fatal("a finished need falls through to the ladder", target, derived)
	}
	p.ResearchTarget = "Batteries"
	if target, derived := ResearchGoal(p, []string{"Smithing"}, census); target != "Batteries" || derived {
		t.Fatal("the operator target wins", target, derived)
	}
	p.ResearchTarget = ""
	p.ResearchLadder = []string{"Unlisted", "Batteries"}
	if target, _ := ResearchGoal(p, nil, census); target != "Batteries" {
		t.Fatal("an unlisted rung is skipped", target)
	}
	p.ResearchLadder = nil
	if target, _ := ResearchGoal(p, nil, census); target != "" {
		t.Fatal("no ladder, no target", target)
	}
	p.ResearchLadder = []string{" "}
	if err := p.Validate(); err == nil {
		t.Fatal("invalid rung accepted")
	}
}

// A ladder rung is a deficit while the research tab is idle and recovers
// once any project is current: the roadmap never replaces a player's own
// choice and holds no development slot while research runs.
func TestReviewMeasuresTheLadderAgainstTheCensus(t *testing.T) {
	f := stableRoutine()
	f.Research = domain.Known(ResearchFacts{Projects: []ResearchProjectID{"Stonecutting", "Electricity"}})
	r := needs(t, f, RoutineLatches{})
	if assessment(t, r, EnsureResearch) != domain.NeedDeficit || !hasNeed(r, EnsureResearch) {
		t.Fatal("idle tab with a rung remaining", r.Assessments)
	}
	for _, g := range r.Goals {
		if g.ID == EnsureResearch && g.Deficit != domain.Known(1.0) {
			t.Fatal("ladder deficit", g)
		}
	}
	f.Research = domain.Known(ResearchFacts{Projects: []ResearchProjectID{"Stonecutting", "Electricity"}, Current: "Electricity"})
	if r = needs(t, f, RoutineLatches{}); assessment(t, r, EnsureResearch) != domain.NeedRecovered {
		t.Fatal("a current project recovers a ladder rung", r.Assessments)
	}
	f.Research = domain.Known(ResearchFacts{Projects: []ResearchProjectID{"Stonecutting", "Electricity"}, Finished: []ResearchProjectID{"Stonecutting", "Electricity"}})
	if r = needs(t, f, RoutineLatches{}); assessment(t, r, EnsureResearch) != domain.NeedRecovered {
		t.Fatal("every listed rung finished", r.Assessments)
	}
	f.Research = domain.Unknown[ResearchFacts]()
	if r = needs(t, f, RoutineLatches{}); assessment(t, r, EnsureResearch) != domain.NeedRecovered {
		t.Fatal("no census, no roadmap", r.Assessments)
	}
}

func TestResearchGateNamesTheFirstUnfinishedRequirement(t *testing.T) {
	facts := domain.Known(ResearchFacts{Finished: []ResearchProjectID{"Stonecutting"}})
	if got := ResearchGate(nil, facts); got != "" {
		t.Fatal(got)
	}
	if got := ResearchGate([]string{"Stonecutting", "Electricity"}, facts); got != "Electricity" {
		t.Fatal(got)
	}
	if got := ResearchGate([]string{"Stonecutting"}, facts); got != "" {
		t.Fatal("finished requirement is no gate", got)
	}
	if got := ResearchGate([]string{"Electricity"}, domain.Unknown[ResearchFacts]()); got != "Electricity" {
		t.Fatal("unknown facts keep the requirement", got)
	}
}
