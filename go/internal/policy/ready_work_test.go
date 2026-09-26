package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func readySnap(plan domain.PlanID) domain.GenerationSnapshot {
	return domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: plan, Revision: 1}
}

func readyBuilding(t *testing.T, id domain.ActionID, def string, x int32) domain.Action {
	t.Helper()
	b, err := domain.NewBuilding(def, domain.Cell{X: x, Z: 1}, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewBuildingAction(id, b)
	if err != nil {
		t.Fatal(err)
	}
	return a
}

func readyBill(t *testing.T, id domain.ActionID) domain.Action {
	t.Helper()
	b, err := domain.NewProductionBill("bench1", "Make_Kibble", "token", domain.FoodTarget, 20)
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewProductionBillAction(id, b)
	if err != nil {
		t.Fatal(err)
	}
	return a
}

// readyProgress advances action id of spec to stage ("" = pending,
// "dispatched" with effect pending, "completed").
func readyProgress(t *testing.T, spec domain.PlanSpec, id domain.ActionID, stage string) domain.Progress {
	t.Helper()
	snap := readySnap(spec.ID())
	p, err := domain.NewProgress(spec, id)
	if err != nil {
		t.Fatal(err)
	}
	if stage == "" {
		return p
	}
	if p, err = p.Prepare(snap, 10); err == nil {
		p, err = p.MarkDispatched(snap, 10)
	}
	if err == nil {
		p, err = p.RecordReceipt(1, domain.ReceiptAccepted)
	}
	effect := domain.EffectPending
	if stage == "completed" {
		effect = domain.EffectCompleted
	}
	if err == nil {
		p, err = p.Observe(domain.Observation{Action: id, Attempt: 1, Snapshot: snap, Tick: 11, Effect: effect, Causality: domain.AfterDispatch}, snap)
	}
	if err != nil {
		t.Fatal(err)
	}
	return p
}

func byStage(r ReadyWorkReport) map[string]ReadyWork {
	out := map[string]ReadyWork{}
	for _, c := range r.Candidates {
		out[c.Stage] = c
	}
	return out
}

func TestReadyWorkStatesNeverClaimReadinessFromMissingFacts(t *testing.T) {
	r := ProjectReadyWork(ReadyRequest{Snapshot: readySnap(""), Unserved: []GoalID{"G"}, Proposals: []ReadyProposal{
		{Goal: "A", Stage: "haul:Steel", Work: WorkHauling, Eligible: domain.Unknown[bool]()},
		{Goal: "B", Stage: "cut_plant:TreeOak", Work: WorkPlantCutting, Eligible: domain.Known(false)},
		{Goal: "C", Stage: "building:Wall", Work: WorkConstruction, Eligible: domain.Known(true), Parallelism: 9},
		{Goal: "D", Stage: "building:Door", Work: WorkConstruction, Eligible: domain.Known(true), Requires: []string{"building:Wall"}},
	}})
	got := byStage(r)
	want := map[string]ReadyState{"none": ReadyNoMethod, "haul:Steel": ReadyAwaiting, "cut_plant:TreeOak": ReadyBlocked, "building:Wall": ReadyRunnable, "building:Door": ReadyBlocked}
	for stage, s := range want {
		if got[stage].State != s {
			t.Fatalf("%s: %+v", stage, got[stage])
		}
		if s != ReadyRunnable && got[stage].Parallelism != 0 {
			t.Fatalf("%s claims workers: %+v", stage, got[stage])
		}
	}
	if got["building:Wall"].Parallelism != maxReadyParallelism {
		t.Fatalf("parallelism unbounded: %+v", got["building:Wall"])
	}
	if d := r.Demand(); !reflect.DeepEqual(d, map[WorkType]int{WorkConstruction: maxReadyParallelism}) {
		t.Fatalf("demand %v", d)
	}
}

func TestReadyWorkExposesIndependentWallBesideBlockedBed(t *testing.T) {
	floor, bed, wall := readyBuilding(t, "floor", "WoodPlankFloor", 1), readyBuilding(t, "bed", "Bed", 2), readyBuilding(t, "wall", "Wall", 3)
	spec, err := domain.NewPlan("p", 1, []domain.Action{floor, bed, wall}, domain.ActionDependency{Action: "bed", Requires: "floor"})
	if err != nil {
		t.Fatal(err)
	}
	// The floor blueprint is out; the bed (first open action after it)
	// waits on the floor, the wall stands alone.
	plan := ReadyPlan{Goal: MaintainSleeping, Spec: spec, Progress: []domain.Progress{readyProgress(t, spec, "floor", "dispatched"), readyProgress(t, spec, "bed", ""), readyProgress(t, spec, "wall", "")}}
	got := byStage(ProjectReadyWork(ReadyRequest{Snapshot: readySnap("p"), Plans: []ReadyPlan{plan}}))
	if c := got["building:Bed"]; c.State != ReadyBlocked || !reflect.DeepEqual(c.Requires, []string{"floor"}) {
		t.Fatalf("bed %+v", c)
	}
	if c := got["building:Wall"]; c.State != ReadyRunnable || c.Parallelism != 1 || c.Work[0] != WorkConstruction {
		t.Fatalf("wall %+v", c)
	}
	if c := got["building:WoodPlankFloor"]; c.State != ReadyRunnable {
		t.Fatalf("floor blueprint %+v", c)
	}
	plan.Progress[0] = readyProgress(t, spec, "floor", "completed")
	got = byStage(ProjectReadyWork(ReadyRequest{Snapshot: readySnap("p"), Plans: []ReadyPlan{plan}}))
	if _, ok := got["building:WoodPlankFloor"]; ok || got["building:Bed"].State != ReadyRunnable {
		t.Fatalf("after floor %+v", got)
	}
}

func TestReadyWorkPlacedBillWaitingOnIngredientsClaimsNoCook(t *testing.T) {
	bill := readyBill(t, "bill")
	spec, err := domain.NewPlan("feed", 1, []domain.Action{bill})
	if err != nil {
		t.Fatal(err)
	}
	plan := ReadyPlan{Goal: MaintainAnimalFeed, Spec: spec, Progress: []domain.Progress{readyProgress(t, spec, "bill", "dispatched")}}
	for _, tc := range []struct {
		inputs map[domain.ActionID]domain.Fact[bool]
		want   ReadyState
	}{
		{nil, ReadyAwaiting},
		{map[domain.ActionID]domain.Fact[bool]{"bill": domain.Known(false)}, ReadyOpenEffect},
		{map[domain.ActionID]domain.Fact[bool]{"bill": domain.Known(true)}, ReadyRunnable},
	} {
		plan.Inputs = tc.inputs
		r := ProjectReadyWork(ReadyRequest{Snapshot: readySnap("feed"), Plans: []ReadyPlan{plan}})
		c := byStage(r)["bill:Make_Kibble"]
		if c.State != tc.want || c.Work[0] != WorkCooking {
			t.Fatalf("%v: %+v", tc.inputs, c)
		}
		if busy := r.Demand()[WorkCooking]; (tc.want == ReadyRunnable) != (busy == 1) {
			t.Fatalf("%v: cook demand %d", tc.inputs, busy)
		}
	}
}

func TestReadyWorkFeedAlternativesAndSharedHaulsDeduplicate(t *testing.T) {
	m := AnimalFeedMethod{Resource: AnimalFeedFallbackResource, Benches: []string{"b1", "b2"}}
	props := AnimalFeedProposals(MaintainAnimalFeed, m, domain.Known(true), []domain.Cell{{X: 5, Z: 5}})
	props = append(props, SupplyHaulProposals("GoalA", "haul", "Steel", []string{"s1", "s2"}, domain.Known(true))...)
	props = append(props, SupplyHaulProposals("GoalB", "haul", "Steel", []string{"s2"}, domain.Known(true))...)
	before := append([]ReadyProposal(nil), props...)
	r := ProjectReadyWork(ReadyRequest{Snapshot: readySnap(""), Proposals: props})
	if !reflect.DeepEqual(before, props) {
		t.Fatal("projection mutated its inputs")
	}
	var feed, hauls int
	for _, c := range r.Candidates {
		if c.Alternative != "" {
			feed++
		}
		if c.Stage == "haul:Steel" {
			hauls++
			if c.Claims[0].Key == "s2" && !reflect.DeepEqual(c.Goals, []GoalID{"GoalA", "GoalB"}) {
				t.Fatalf("shared haul %+v", c)
			}
		}
	}
	if feed != 3 || hauls != 2 {
		t.Fatalf("feed %d hauls %d: %+v", feed, hauls, r.Candidates)
	}
	// Kibble and hay are different stages, not one OR over the profile;
	// only one alternative is demanded.
	d := r.Demand()
	if d[WorkCooking]+d[WorkGrowing] != 1 || d[WorkHauling] != 2 {
		t.Fatalf("demand %v", d)
	}
}

func TestReadyWorkStableBoundedAndWorldScoped(t *testing.T) {
	trees := []string{"t1", "t2", "t3", "t4", "t5"}
	props := WoodProposals(MaintainWood, "cut", "TreeOak", trees, domain.Known(true))
	bounds := ReadyBounds{Candidates: 3, PerGoal: 4, Discovery: 10}
	a := ProjectReadyWork(ReadyRequest{Snapshot: readySnap(""), Proposals: props, Bounds: bounds})
	rev := append([]ReadyProposal(nil), props...)
	for i, j := 0, len(rev)-1; i < j; i, j = i+1, j-1 {
		rev[i], rev[j] = rev[j], rev[i]
	}
	b := ProjectReadyWork(ReadyRequest{Snapshot: readySnap(""), Proposals: rev, Bounds: bounds})
	if !reflect.DeepEqual(a, b) {
		t.Fatalf("order-dependent:\n%+v\n%+v", a, b)
	}
	if len(a.Candidates) != 3 {
		t.Fatalf("candidates %d", len(a.Candidates))
	}
	want := []ReadyDeferral{{Goal: MaintainWood, Reason: ReadyDeferredCandidate, Count: 1}, {Goal: MaintainWood, Reason: ReadyDeferredPerGoal, Count: 1}}
	if !reflect.DeepEqual(a.Deferred, want) {
		t.Fatalf("deferred %+v", a.Deferred)
	}
	small := ProjectReadyWork(ReadyRequest{Snapshot: readySnap(""), Proposals: props, Bounds: ReadyBounds{Candidates: 9, PerGoal: 9, Discovery: 2}})
	if len(small.Candidates) != 2 || small.Continuation == "" {
		t.Fatalf("discovery bound %+v", small)
	}
	other := readySnap("")
	other.Load = "load2"
	c := ProjectReadyWork(ReadyRequest{Snapshot: other, Proposals: props, Bounds: bounds})
	if c.Candidates[0].ID == a.Candidates[0].ID || a.Current(other) || !a.Current(readySnap("x")) {
		t.Fatal("world change kept candidate identity")
	}
}

func TestReadyWorkUnmigratedFamilyIsConservative(t *testing.T) {
	rep, err := domain.NewRepair("pawn1", "wall1", domain.Cell{X: 1, Z: 1})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewRepairAction("r", rep)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan("rep", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	r := ProjectReadyWork(ReadyRequest{Snapshot: readySnap("rep"), Plans: []ReadyPlan{{Goal: MaintainEssentialRepairs, Spec: spec, Progress: []domain.Progress{readyProgress(t, spec, "r", "dispatched")}}}})
	c := r.Candidates[0]
	if !reflect.DeepEqual(r.Conservative, []domain.ActionKind{domain.RepairAction}) || c.Adapter != ReadyConservative || c.State != ReadyRunnable || c.Parallelism != 1 || !reflect.DeepEqual(c.Work, GoalLabor(MaintainEssentialRepairs)) {
		t.Fatalf("%+v", r)
	}
}
