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

func census(workers ...DevelopmentWorker) domain.Fact[[]DevelopmentWorker] {
	return domain.Known(workers)
}

func worker(id string, work ...WorkType) DevelopmentWorker {
	return DevelopmentWorker{ID: PawnID(id), Work: work}
}

func autoFixture() DevelopmentRequest {
	return DevelopmentRequest{
		Snapshot: domain.GenerationSnapshot{Colony: "colony", Map: 1, Load: "load", Plan: "plan"}, Tick: 100,
		Workers: domain.Known(5),
		Census:  census(worker("a", WorkConstruction, WorkHauling), worker("b", WorkConstruction), worker("c", WorkResearch), worker("d", WorkPlantCutting), worker("e", WorkHauling, WorkCooking)),
		Goals: []DevelopmentGoal{
			{ID: "build", Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(1.0), Labor: LaborProfile{WorkConstruction}},
			{ID: "study", Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(.9), Labor: LaborProfile{WorkResearch}},
			{ID: "wood", Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(.8), Labor: LaborProfile{WorkPlantCutting}},
			{ID: "haul", Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(.7), Labor: LaborProfile{WorkHauling}},
			{ID: "shed", Source: AutopilotGoal, Priority: 4, Deficit: domain.Known(.6), Labor: LaborProfile{WorkConstruction}},
		},
	}
}

func rowOf(s DevelopmentState, goal GoalID) DevelopmentRow {
	for _, row := range s.Rows {
		if row.Goal == goal {
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
		{Goal: "keep-a", Source: AutopilotGoal, Priority: 3, Progress: actionProgress(t, "ka"), Labor: LaborProfile{WorkConstruction}},
		{Goal: "keep-b", Source: AutopilotGoal, Priority: 4, Progress: actionProgress(t, "kb"), Labor: LaborProfile{WorkCooking}},
	}
	requireSelected(t, rank(t, r), "build", "study", "wood", "haul", "shed")
	r.Commitments = nil
	requireSelected(t, rank(t, r), "build", "study", "wood", "haul", "shed")
}

// A goal with no method is not selected, but holds back no other.
func TestAutoDevelopmentSkipsInfeasibleHighRank(t *testing.T) {
	r := autoFixture()
	r.Goals[0].MethodUnavailable = true
	s := rank(t, r)
	requireSelected(t, s, "study", "wood", "haul", "shed")
	if row := rowOf(s, "build"); row.Reason != DevelopmentMethodUnavailable {
		t.Fatal(row)
	}
}

// More open startup jobs than pawns do not stop a goal being admitted.
func TestAutoDevelopmentStartupHoldsNoGoalBack(t *testing.T) {
	r := autoFixture()
	r.Census = census(worker("a", WorkConstruction))
	r.Workers = domain.Known(1)
	r.Goals = r.Goals[:1]
	r.Commitments = []Commitment{
		{Goal: "shelter", Source: AutopilotGoal, Priority: 2, Progress: actionProgress(t, "s"), Labor: LaborProfile{WorkConstruction}},
		{Goal: "beds", Source: AutopilotGoal, Priority: 2, Progress: actionProgress(t, "b"), Labor: LaborProfile{WorkConstruction}},
	}
	requireSelected(t, rank(t, r), "build")
}

// Admission agrees with the ranking and checks no capacity.
func TestAdmitDevelopmentAgreesWithRank(t *testing.T) {
	r := autoFixture()
	s := rank(t, r)
	for _, g := range []GoalID{"build", "study", "wood", "haul", "shed"} {
		if err := AdmitDevelopment(s, g, s.Holds); err != nil {
			t.Fatal(g, err)
		}
	}
	if err := AdmitDevelopment(s, "unranked", s.Holds); err == nil {
		t.Fatal("unranked goal admitted")
	}
	s.Capacity = 1
	if err := AdmitDevelopment(s, "study", nil); err != nil {
		t.Fatal(err)
	}
}

func TestDevelopmentCensus(t *testing.T) {
	yes, no := domain.Known(true), domain.Known(false)
	pawns := []WorkPawn{
		{ID: "b", Available: yes, Applies: yes, Work: domain.Known([]WorkPriority{{Work: WorkHauling, Priority: 3}, {Work: WorkMining, Priority: 1}, {Work: WorkCooking, Priority: 0}}), Incapable: domain.Known([]WorkType{WorkMining})},
		{ID: "a", Available: yes, Applies: yes, Work: domain.Known([]WorkPriority{{Work: WorkConstruction, Priority: 1}})},
		{ID: "down", Available: no, Applies: yes},
	}
	got, known := DevelopmentCensus(pawns).Value()
	if !known || len(got) != 2 || got[0].ID != "a" || len(got[1].Work) != 1 || got[1].Work[0] != WorkHauling {
		t.Fatal(got)
	}
	pawns = append(pawns, WorkPawn{ID: "c", Available: yes, Applies: yes})
	if _, known := DevelopmentCensus(pawns).Value(); known {
		t.Fatal("unknown settings made a known census")
	}
}
