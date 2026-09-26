package policy

import (
	"strings"
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
		Workers: domain.Known(5), Limit: MaxAutoDevelopmentProjects, Auto: true,
		Census: census(worker("a", WorkConstruction, WorkHauling), worker("b", WorkConstruction), worker("c", WorkResearch), worker("d", WorkPlantCutting), worker("e", WorkHauling, WorkCooking)),
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

// Automatic mode admits every independent project a distinct worker can
// take, past two, while two unrelated maintained goals keep their open
// work; the explicit limit keeps its slot count.
func TestAutoDevelopmentAdmitsPastTwoWithOpenGoals(t *testing.T) {
	r := autoFixture()
	r.Commitments = []Commitment{
		{Goal: "keep-a", Source: AutopilotGoal, Priority: 3, Progress: actionProgress(t, "ka"), Labor: LaborProfile{WorkConstruction}},
		{Goal: "keep-b", Source: AutopilotGoal, Priority: 4, Progress: actionProgress(t, "kb"), Labor: LaborProfile{WorkCooking}},
	}
	s := rank(t, r)
	// keep-a takes a builder (a or b), keep-b the cook (e): build takes the
	// other builder, study, wood; haul finds no hauler left, shed no builder.
	requireSelected(t, s, "build", "study", "wood")
	if row := rowOf(s, "haul"); row.Reason != DevelopmentLabor || row.Bottleneck != WorkHauling {
		t.Fatal(row)
	}
	if s.Limiting != DevelopmentLabor || s.Unused != domain.Known(0) || len(s.Holds) != 2 || !s.Holds[0].Slot {
		t.Fatalf("%+v", s)
	}
	if err := ValidateDevelopmentState(s); err != nil {
		t.Fatal(err)
	}
	// An explicit limit of two is the documented slot count: both open
	// goals fill it.
	explicit := r
	explicit.Auto, explicit.Limit = false, 2
	s = rank(t, explicit)
	requireSelected(t, s)
	if rowOf(s, "build").Reason != DevelopmentCapacity {
		t.Fatal(rowOf(s, "build"))
	}
	// Without the open goals, automatic mode fills every compatible worker.
	r.Commitments = nil
	s = rank(t, r)
	requireSelected(t, s, "build", "study", "wood", "haul", "shed")
}

// A high-ranked goal that has no method, is refused or lacks its labor does
// not strand a lower-ranked feasible one: it holds no worker.
func TestAutoDevelopmentSkipsInfeasibleHighRank(t *testing.T) {
	r := autoFixture()
	r.Census = census(worker("a", WorkConstruction), worker("b", WorkPlantCutting))
	r.Workers = domain.Known(2)
	r.Goals[0].MethodUnavailable = true
	s := rank(t, r)
	requireSelected(t, s, "wood", "shed")
	if row := rowOf(s, "study"); row.Reason != DevelopmentLabor || row.Bottleneck != WorkResearch {
		t.Fatal(row)
	}
	// A selected goal whose planner finds nothing yields: its builder goes
	// to the next goal the same fit accepts, never to a labor-less one.
	r.Goals[0].MethodUnavailable = false
	s = rank(t, r)
	requireSelected(t, s, "build", "wood")
	if rowOf(s, "shed").Reason != DevelopmentLabor {
		t.Fatal(rowOf(s, "shed"))
	}
	y := YieldDevelopment(s, "build")
	requireSelected(t, y, "wood", "shed")
	if !rowOf(y, "shed").Granted || rowOf(y, "study").Selected || y.Yields != 1 {
		t.Fatalf("%+v", y.Rows)
	}
	if err := ValidateDevelopmentState(y); err != nil {
		t.Fatal(err)
	}
}

// A yield never grants a stage-held or risky row, and regrants per review
// are bounded.
func TestYieldKeepsStageRiskAndBound(t *testing.T) {
	r := autoFixture()
	r.Census = census(worker("a", WorkConstruction, WorkResearch))
	r.Workers = domain.Known(1)
	r.Stage = ColonyStageRecord{Stage: StageFoothold, Held: true}
	r.Goals = []DevelopmentGoal{
		{ID: "study", Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(1.0), Labor: LaborProfile{WorkResearch}},
		{ID: EnsureComfort, Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(.9), Labor: GoalLabor(EnsureComfort)},
		{ID: "risky", Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(.8), Labor: LaborProfile{WorkConstruction}, Risk: domain.Known(1.0)},
	}
	s := rank(t, r)
	requireSelected(t, s, "study")
	y := YieldDevelopment(s, "study")
	requireSelected(t, y)
	if rowOf(y, EnsureComfort).Selected || rowOf(y, "risky").Reason != DevelopmentRisk {
		t.Fatalf("%+v", y.Rows)
	}
	s.Yields = MaxDevelopmentYields
	y = YieldDevelopment(s, "study")
	if y.Continuation != DevelopmentYieldBound || len(selected(y)) != 0 {
		t.Fatalf("%+v", y)
	}
	if err := ValidateDevelopmentState(y); err != nil {
		t.Fatal(err)
	}
}

// Startup work takes no slot but holds its worker in automatic mode; open
// work beyond the census pauses new admissions without releasing it.
func TestAutoDevelopmentStartupHoldsWorkers(t *testing.T) {
	r := autoFixture()
	r.Census = census(worker("a", WorkConstruction), worker("b", WorkConstruction))
	r.Workers = domain.Known(2)
	r.Goals = r.Goals[:1]
	r.Commitments = []Commitment{{Goal: "shelter", Source: AutopilotGoal, Priority: 2, Progress: actionProgress(t, "s"), Labor: LaborProfile{WorkConstruction}}}
	requireSelected(t, rank(t, r), "build")
	r.Commitments = append(r.Commitments, Commitment{Goal: "beds", Source: AutopilotGoal, Priority: 2, Progress: actionProgress(t, "b"), Labor: LaborProfile{WorkConstruction}})
	s := rank(t, r)
	requireSelected(t, s)
	if row := rowOf(s, "build"); row.Reason != DevelopmentLabor {
		t.Fatal(row)
	}
	// A worker left: the two open startup jobs exceed the census.
	r.Census, r.Workers = census(worker("a", WorkConstruction)), domain.Known(1)
	s = rank(t, r)
	if row := rowOf(s, "build"); row.Reason != DevelopmentOvercommitted || s.Limiting != DevelopmentOvercommitted {
		t.Fatal(row)
	}
	// Explicit mode keeps the per-type headcount: startup work holds no slot
	// and no labor there.
	r.Auto, r.Limit, r.Labor = false, 2, domain.Known(map[WorkType]int{WorkConstruction: 1})
	requireSelected(t, rank(t, r), "build")
}

// Admission refits what the ranking fitted, against commitments read at
// admission time.
func TestAdmitDevelopmentAgreesWithRank(t *testing.T) {
	r := autoFixture()
	r.Census = census(worker("a", WorkConstruction), worker("b", WorkConstruction, WorkResearch))
	r.Workers = domain.Known(3)
	r.Goals = r.Goals[:2]
	s := rank(t, r)
	requireSelected(t, s, "build", "study")
	for _, g := range []GoalID{"build", "study"} {
		if err := AdmitDevelopment(s, g, s.Holds); err != nil {
			t.Fatal(g, err)
		}
	}
	if err := AdmitDevelopment(s, "wood", s.Holds); err == nil {
		t.Fatal("unselected goal admitted")
	}
	// A player project accepted since the ranking took a builder: build
	// is admitted first (rank order), then study finds b taken.
	player := CommitmentHolds([]Commitment{{Goal: "player-project-x", Source: PlayerGoal, Priority: 3, Progress: actionProgress(t, "p"), Labor: LaborProfile{WorkConstruction}}}, s.Tick, nil, true, nil)
	if err := AdmitDevelopment(s, "build", player); err != nil {
		t.Fatal(err)
	}
	if err := AdmitDevelopment(s, "study", player); err == nil || !strings.Contains(err.Error(), string(DevelopmentLabor)) {
		t.Fatal(err)
	}
	// Once build is admitted it is a hold, not a selected row ahead.
	admitted := CommitmentHolds([]Commitment{{Goal: "build", Source: AutopilotGoal, Priority: 3, Progress: actionProgress(t, "bd"), Labor: LaborProfile{WorkConstruction}}}, s.Tick, nil, true, nil)
	if err := AdmitDevelopment(s, "study", admitted); err != nil {
		t.Fatal(err)
	}
	// The slot bound is checked too.
	s.Capacity = 1
	if err := AdmitDevelopment(s, "study", admitted); err == nil || !strings.Contains(err.Error(), string(DevelopmentCapacity)) {
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
