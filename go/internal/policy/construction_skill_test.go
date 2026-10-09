package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"slices"
	"testing"
)

func skillSite(t *testing.T, name, id string, x int32) ConstructionSite {
	t.Helper()
	a := readyBuilding(t, domain.ActionID(id), name, x)
	b, _ := a.Building()
	return ConstructionSite{ID: id, Building: b, Stage: "frame", ResourcesComplete: domain.Known(true), QualitySensitive: domain.Known(name == "Bed"), NativeFinishingSkill: domain.Known(0)}
}

func TestConstructionSkillChosenOnceFromBestCapable(t *testing.T) {
	bed := skillSite(t, "Bed", "bed", 2)
	census := domain.Known(CurrentConstruction{Colony: true, Sites: []ConstructionSite{bed}})
	pawns := helpTeam()
	for i := range pawns {
		pawns[i].ConstructionAble = domain.Known(true)
	}
	pawns[0].Available = domain.Known(false) // Draft or temporary unavailable status is not skill loss.
	want := ConstructionSkillChoices(census, domain.Known(pawns))
	if len(want) != 1 || want[0].Minimum != 9 {
		t.Fatalf("busy builder must qualify: %+v", want)
	}
	pawns[0].ConstructionAble = domain.Known(false)
	if got := ConstructionSkillChoices(census, domain.Known(pawns)); len(got) != 1 || got[0].Minimum != 2 {
		t.Fatalf("physically incapable skilled pawn set the minimum: %+v", got)
	}
	pawns[0].ConstructionAble = domain.Known(true)
	slices.Reverse(pawns)
	if got := ConstructionSkillChoices(census, domain.Known(pawns)); len(got) != 1 || got[0].Minimum != 9 {
		t.Fatal(got)
	}
	bed.MinimumFinishingSkill = domain.Known(9)
	census = domain.Known(CurrentConstruction{Colony: true, Sites: []ConstructionSite{bed}})
	if got := ConstructionSkillChoices(census, domain.Known(pawns[:2])); len(got) != 0 {
		t.Fatalf("lost builder lowered configured minimum: %+v", got)
	}
	bed.MinimumFinishingSkill = domain.Unknown[int]()
	bed.NativeFinishingSkill = domain.Known(12)
	if got := ConstructionSkillChoices(domain.Known(CurrentConstruction{Colony: true, Sites: []ConstructionSite{bed}}), domain.Known(pawns)); len(got) != 0 {
		t.Fatalf("no builder meets native prerequisite: %+v", got)
	}
	if got := ConstructionSkillChoices(census, domain.Unknown[[]WorkPawn]()); len(got) != 0 {
		t.Fatal(got)
	}
}

func TestConstructionHelpersUseFilledFramesBesideProtectedFurniture(t *testing.T) {
	wall := skillSite(t, "Wall", "wall", 3)
	bed := skillSite(t, "Bed", "bed", 5)
	bed.MinimumFinishingSkill = domain.Known(9)
	build := readyBuilding(t, "wall", "Wall", 3)
	spec, err := domain.NewPlan("p", 1, []domain.Action{build})
	if err != nil {
		t.Fatal(err)
	}
	plan := ReadyPlan{Concern: MaintainHousing, Spec: spec, Progress: []domain.Progress{readyProgress(t, spec, "wall", "completed")}}
	census := domain.Known(CurrentConstruction{Colony: true, Sites: []ConstructionSite{wall, bed}})
	report := ProjectReadyWork(ReadyRequest{Snapshot: readySnap("p"), Plans: []ReadyPlan{plan}, Construction: census})
	previous := &ConstructionHelpRecord{Tick: 100, Idle: []PawnID{"a", "b"}}
	help := ConstructionHelpDemand(&report, readySnap("p"), 700, []string{"Wall", "Bed"}, previous, census)
	decision := planHelp(t, helpTeam(), help)
	if decision.Help.Ready != 1 || !slices.Equal(decision.Help.Helpers, []PawnID{"a"}) {
		t.Fatalf("filled wall should use idle helper: %+v", decision.Help)
	}
	decision, err = PlanWork(helpTeam(), []WorkRequirement{{Work: WorkConstruction, Skill: "Construction", Minimum: 8}}, WorkDemand{Construction: true, Help: &help})
	if err != nil || decision.Help == nil || !slices.Equal(decision.Help.Helpers, []PawnID{"a"}) {
		t.Fatalf("unrelated guarded furniture prerequisite blocked safe wall: %+v %v", decision.Help, err)
	}
	ready := ConstructionHelperView(&report, census, readySnap("p"))
	wall.ResourcesComplete = domain.Known(false)
	blocked := ConstructionHelperView(ready, domain.Known(CurrentConstruction{Colony: true, Sites: []ConstructionSite{wall}}), readySnap("p"))
	if got, _ := ConstructionHelpDemand(blocked, readySnap("p"), 700, nil, nil).Ready.Value(); got != 0 {
		t.Fatalf("stale ready work survived native material shortage: %+v", blocked)
	}
	wall.ResourcesComplete = domain.Known(true)
	// Missing readback never lifts the coarse quality safeguard, including player sites.
	bed.MinimumFinishingSkill = domain.Unknown[int]()
	census = domain.Known(CurrentConstruction{Colony: true, Sites: []ConstructionSite{wall, bed}})
	if got := planHelp(t, helpTeam(), ConstructionHelpDemand(&report, readySnap("p"), 700, nil, previous, census)); got.Help.Reason != HelpRiskyTask {
		t.Fatal(got.Help)
	}
	// Observed material shortage or unknown resources never offers speculative work.
	for _, resources := range []domain.Fact[bool]{domain.Known(false), domain.Unknown[bool]()} {
		wall.ResourcesComplete = resources
		census = domain.Known(CurrentConstruction{Colony: true, Sites: []ConstructionSite{wall}})
		got := planHelp(t, helpTeam(), ConstructionHelpDemand(&report, readySnap("p"), 700, []string{"Wall"}, previous, census))
		if got.Help.Ready != 0 || len(got.Help.Helpers) != 0 {
			t.Fatal(got.Help)
		}
	}
}
