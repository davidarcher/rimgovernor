package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func actionProgress(t *testing.T, id string) domain.Progress {
	t.Helper()
	b, e := domain.NewBuilding("Wall", domain.Cell{X: 1, Z: 1}, domain.North, "WoodLog")
	if e != nil {
		t.Fatal(e)
	}
	a, e := domain.NewBuildingAction(domain.ActionID(id), b)
	if e != nil {
		t.Fatal(e)
	}
	p, e := domain.NewPlan(domain.PlanID("plan-"+id), 0, []domain.Action{a})
	if e != nil {
		t.Fatal(e)
	}
	v, e := domain.NewProgress(p, a.ID())
	if e != nil {
		t.Fatal(e)
	}
	return v
}

func autoFixture() DevelopmentRequest {
	return DevelopmentRequest{
		Snapshot: domain.GenerationSnapshot{Colony: "colony", Map: 1, Load: "load", Plan: "plan"}, Tick: 100,
		Workers: domain.Known(5),
		Concerns: []DevelopmentConcern{
			{ID: "build", Priority: 3, Deficit: domain.Known(1.0), Labor: LaborProfile{WorkConstruction}},
			{ID: "study", Priority: 3, Deficit: domain.Known(.9), Labor: LaborProfile{WorkResearch}},
			{ID: "wood", Priority: 3, Deficit: domain.Known(.8), Labor: LaborProfile{WorkPlantCutting}},
			{ID: "haul", Priority: 3, Deficit: domain.Known(.7), Labor: LaborProfile{WorkHauling}},
			{ID: "shed", Priority: 4, Deficit: domain.Known(.6), Labor: LaborProfile{WorkConstruction}},
		},
	}
}

func rowOf(s DevelopmentState, goal ConcernID) DevelopmentRow {
	for _, row := range s.Rows {
		if row.Concern == goal {
			return row
		}
	}
	return DevelopmentRow{}
}

// Automatic mode admits every independent project, whatever the open work
// already holds: there is no slot or worker limit.
func TestAutoDevelopmentAdmitsEveryGoal(t *testing.T) {
	r := autoFixture()
	r.Commitments = []Commitment{
		{Concern: "keep-a", Priority: 3, Progress: actionProgress(t, "ka"), Labor: LaborProfile{WorkConstruction}},
		{Concern: "keep-b", Priority: 4, Progress: actionProgress(t, "kb"), Labor: LaborProfile{WorkCooking}},
	}
	requireSelected(t, rank(t, r), "build", "study", "wood", "haul", "shed")
	r.Commitments = nil
	requireSelected(t, rank(t, r), "build", "study", "wood", "haul", "shed")
}

// A goal with no method is not selected, but holds back no other.
func TestAutoDevelopmentSkipsInfeasibleHighRank(t *testing.T) {
	r := autoFixture()
	r.Concerns[0].MethodUnavailable = true
	s := rank(t, r)
	requireSelected(t, s, "study", "wood", "haul", "shed")
	if row := rowOf(s, "build"); row.Reason != DevelopmentMethodUnavailable {
		t.Fatal(row)
	}
}

// More open startup jobs than pawns do not stop a goal being admitted.
func TestAutoDevelopmentStartupHoldsNoGoalBack(t *testing.T) {
	r := autoFixture()
	r.Workers = domain.Known(1)
	r.Concerns = r.Concerns[:1]
	r.Commitments = []Commitment{
		{Concern: "shelter", Priority: 2, Progress: actionProgress(t, "s"), Labor: LaborProfile{WorkConstruction}},
		{Concern: "beds", Priority: 2, Progress: actionProgress(t, "b"), Labor: LaborProfile{WorkConstruction}},
	}
	requireSelected(t, rank(t, r), "build")
}

// Admission agrees with the ranking.
func TestAdmitDevelopmentAgreesWithRank(t *testing.T) {
	r := autoFixture()
	s := rank(t, r)
	for _, g := range []ConcernID{"build", "study", "wood", "haul", "shed"} {
		if err := AdmitDevelopment(s, g); err != nil {
			t.Fatal(g, err)
		}
	}
	if err := AdmitDevelopment(s, "unranked"); err == nil {
		t.Fatal("unranked goal admitted")
	}
}
