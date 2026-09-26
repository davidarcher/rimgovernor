package policy

import (
	"fmt"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func allocWorker(id string, work ...WorkType) AllocWorker {
	var ps []WorkPriority
	for _, w := range work {
		ps = append(ps, WorkPriority{Work: w, Priority: 3})
	}
	return AllocWorker{ID: PawnID(id), Status: AllocAvailable, Occupancy: OccupancyIdle, Work: domain.Known(ps)}
}

func allocCand(id string, work WorkType, parallel int) ReadyWork {
	return ReadyWork{ID: ReadyWorkID(id), Stage: string(work), Work: LaborProfile{work}, State: ReadyRunnable, Parallelism: parallel}
}

func allocReady(cs ...ReadyWork) ReadyWorkReport { return ReadyWorkReport{Candidates: cs} }

func assigned(r AllocationReport) map[PawnID]ReadyWorkID {
	out := map[PawnID]ReadyWorkID{}
	for _, a := range r.Assignments {
		out[a.Pawn] = a.Work
	}
	return out
}

func checkDistinct(t *testing.T, r AllocationReport) {
	t.Helper()
	seen := map[PawnID]bool{}
	for _, a := range r.Assignments {
		if seen[a.Pawn] {
			t.Fatalf("pawn %s assigned twice: %+v", a.Pawn, r.Assignments)
		}
		seen[a.Pawn] = true
	}
}

func TestAllocateMultiSkilledPawnIsOneWorker(t *testing.T) {
	r := AllocateWorkers(AllocRequest{
		Workers: []AllocWorker{allocWorker("a", WorkCooking, WorkGrowing, WorkHauling)},
		Ready:   allocReady(allocCand("cook", WorkCooking, 1), allocCand("grow", WorkGrowing, 1), allocCand("haul", WorkHauling, 1)),
	})
	checkDistinct(t, r)
	if len(r.Assignments) != 1 || r.Assignments[0].Work != "cook" {
		t.Fatalf("assignments = %+v", r.Assignments)
	}
	if len(r.Unfilled) != 2 || r.Unfilled[0].Reason != AllocReasonWorkersTaken {
		t.Fatalf("unfilled = %+v", r.Unfilled)
	}
}

func TestAllocateRematchesFlexibleWorker(t *testing.T) {
	// A can cook or build, B only cooks. Cooking ranks first, and A would
	// take it greedily, stranding B.
	r := AllocateWorkers(AllocRequest{
		Workers: []AllocWorker{allocWorker("a", WorkCooking, WorkConstruction), allocWorker("b", WorkCooking)},
		Ready:   allocReady(allocCand("cook", WorkCooking, 1), allocCand("build", WorkConstruction, 1)),
	})
	checkDistinct(t, r)
	if got := assigned(r); !reflect.DeepEqual(got, map[PawnID]ReadyWorkID{"a": "build", "b": "cook"}) {
		t.Fatalf("assigned = %v", got)
	}
}

func TestAllocatePreservesHigherPriorityWhenShort(t *testing.T) {
	// One worker who can do both: the higher-priority candidate wins.
	r := AllocateWorkers(AllocRequest{
		Workers: []AllocWorker{allocWorker("a", WorkCooking, WorkConstruction)},
		Ready:   allocReady(allocCand("build", WorkConstruction, 1), allocCand("cook", WorkCooking, 1)),
	})
	if got := assigned(r); !reflect.DeepEqual(got, map[PawnID]ReadyWorkID{"a": "build"}) {
		t.Fatalf("assigned = %v", got)
	}
}

func TestAllocateEightDisjointSpecialists(t *testing.T) {
	types := []WorkType{WorkConstruction, WorkCooking, WorkGrowing, WorkHauling, WorkMining, WorkPlantCutting, WorkCrafting, WorkResearch}
	var ws []AllocWorker
	var cs []ReadyWork
	for i, w := range types {
		ws = append(ws, allocWorker(fmt.Sprintf("p%d", i), w))
		cs = append(cs, allocCand(string(w), w, 1))
	}
	r := AllocateWorkers(AllocRequest{Workers: ws, Ready: allocReady(cs...)})
	checkDistinct(t, r)
	if len(r.Assignments) != 8 || len(r.Unfilled) != 0 || len(r.Unused) != 0 {
		t.Fatalf("report = %+v", r)
	}
}

func TestAllocateOneBuilderTakesOnePosition(t *testing.T) {
	r := AllocateWorkers(AllocRequest{
		Workers: []AllocWorker{allocWorker("builder", WorkConstruction), allocWorker("cook", WorkCooking)},
		Ready:   allocReady(allocCand("wall", WorkConstruction, 4)),
	})
	if len(r.Assignments) != 1 || r.Assignments[0].Pawn != "builder" || len(r.Unfilled) != 3 {
		t.Fatalf("report = %+v", r)
	}
	if r.Unused[0].Pawn != "cook" || r.Unused[0].Reason != AllocReasonNoDemand {
		t.Fatalf("unused = %+v", r.Unused)
	}
}

func TestAllocateAllOverlapping(t *testing.T) {
	all := []WorkType{WorkConstruction, WorkCooking, WorkHauling}
	r := AllocateWorkers(AllocRequest{
		Workers: []AllocWorker{allocWorker("a", all...), allocWorker("b", all...), allocWorker("c", all...)},
		Ready:   allocReady(allocCand("build", WorkConstruction, 2), allocCand("cook", WorkCooking, 1), allocCand("haul", WorkHauling, 1)),
	})
	checkDistinct(t, r)
	if len(r.Assignments) != 3 || len(r.Unfilled) != 1 || r.Unfilled[0].Work != "haul" {
		t.Fatalf("report = %+v", r)
	}
}

func TestAllocateExcludesNonWorkingAndPreservesUnknown(t *testing.T) {
	drafted := allocWorker("d", WorkCooking)
	drafted.Status = AllocDrafted
	resting := allocWorker("r", WorkCooking)
	resting.Status = AllocResting
	down := allocWorker("u", WorkCooking)
	down.Status = AllocUnavailable
	unknown := AllocWorker{ID: "x", Status: AllocAvailable, Occupancy: OccupancyIdle, Work: domain.Unknown[[]WorkPriority]()}
	r := AllocateWorkers(AllocRequest{Workers: []AllocWorker{drafted, resting, down, unknown}, Ready: allocReady(allocCand("cook", WorkCooking, 1))})
	if len(r.Assignments) != 0 {
		t.Fatalf("assignments = %+v", r.Assignments)
	}
	if r.Unfilled[0].Reason != AllocReasonEligibleUnknown {
		t.Fatalf("unfilled = %+v", r.Unfilled)
	}
	reasons := map[PawnID]string{}
	for _, u := range r.Unused {
		reasons[u.Pawn] = u.Reason
	}
	want := map[PawnID]string{"d": "drafted", "r": "resting", "u": "unavailable", "x": AllocReasonSettingsUnknown}
	if !reflect.DeepEqual(reasons, want) {
		t.Fatalf("unused = %v", reasons)
	}
	// A drafted cook is potential future capacity, not usable now.
	if !reflect.DeepEqual(r.Unused[0].Potential, []WorkType{WorkCooking}) {
		t.Fatalf("potential = %+v", r.Unused[0])
	}
}

func TestAllocateIncapableAndOverride(t *testing.T) {
	a := allocWorker("a", WorkCooking)
	a.Incapable = []WorkType{WorkCooking}
	b := allocWorker("b", WorkCooking)
	c := allocWorker("c")
	r := AllocateWorkers(AllocRequest{
		Workers: []AllocWorker{a, b, c},
		Ready:   allocReady(allocCand("cook", WorkCooking, 2), allocCand("build", WorkConstruction, 1)),
		Bounds:  AllocBounds{Overrides: []WorkOverride{{Pawn: "b", Work: WorkCooking, Priority: 0}, {Pawn: "c", Work: WorkConstruction, Priority: 1}}},
	})
	if got := assigned(r); !reflect.DeepEqual(got, map[PawnID]ReadyWorkID{"c": "build"}) {
		t.Fatalf("assigned = %v", got)
	}
}

func TestAllocateSharedBenchAlternatives(t *testing.T) {
	// Kibble on either of two shared benches: alternatives, so only one is
	// staffed even with two idle cooks.
	b1, b2 := allocCand("bench1", WorkCooking, 1), allocCand("bench2", WorkCooking, 1)
	b1.Alternative, b2.Alternative = "feed", "feed"
	r := AllocateWorkers(AllocRequest{Workers: []AllocWorker{allocWorker("a", WorkCooking), allocWorker("b", WorkCooking)}, Ready: allocReady(b1, b2)})
	if len(r.Assignments) != 1 || r.Assignments[0].Work != "bench1" || r.Unfilled[0].Reason != AllocReasonAlternativeTaken {
		t.Fatalf("report = %+v", r)
	}
}

func TestAllocateNonDevelopmentWorkOccupiesLabor(t *testing.T) {
	busy := allocWorker("a", WorkCooking)
	busy.Occupancy = OccupancyOther
	unseen := allocWorker("b", WorkCooking)
	unseen.Occupancy = OccupancyUnknown
	r := AllocateWorkers(AllocRequest{Workers: []AllocWorker{busy, unseen}, Ready: allocReady(allocCand("cook", WorkCooking, 1))})
	if len(r.Assignments) != 0 || r.Unused[0].Reason != AllocReasonBusy || r.Unused[1].Reason != AllocReasonOccupancyUnknown {
		t.Fatalf("report = %+v", r)
	}
}

func TestAllocateIncumbentKeptAndNotRematched(t *testing.T) {
	// A is already building, B only cooks and cooking outranks building.
	// A stays on its job, and B still cooks.
	a := allocWorker("a", WorkCooking, WorkConstruction)
	a.Occupancy, a.Serving = OccupancyServing, "build"
	r := AllocateWorkers(AllocRequest{
		Workers: []AllocWorker{a, allocWorker("b", WorkCooking)},
		Ready:   allocReady(allocCand("cook", WorkCooking, 1), allocCand("build", WorkConstruction, 1)),
	})
	if got := assigned(r); !reflect.DeepEqual(got, map[PawnID]ReadyWorkID{"a": "build", "b": "cook"}) {
		t.Fatalf("assigned = %v", got)
	}
	for _, x := range r.Assignments {
		if x.Pawn == "a" && x.Reason != AllocReasonIncumbent {
			t.Fatalf("a = %+v", x)
		}
	}
	// Without B, the incumbent still keeps building. It is not pulled off
	// to cook.
	r = AllocateWorkers(AllocRequest{Workers: []AllocWorker{a}, Ready: allocReady(allocCand("cook", WorkCooking, 1), allocCand("build", WorkConstruction, 1))})
	if got := assigned(r); !reflect.DeepEqual(got, map[PawnID]ReadyWorkID{"a": "build"}) {
		t.Fatalf("assigned = %v", got)
	}
}

func TestAllocateCeilingAndRestriction(t *testing.T) {
	ws := []AllocWorker{allocWorker("a", WorkConstruction, WorkHauling), allocWorker("b", WorkConstruction), allocWorker("c", WorkConstruction)}
	r := AllocateWorkers(AllocRequest{
		Workers: ws,
		Ready:   allocReady(allocCand("build", WorkConstruction, 3), allocCand("haul", WorkHauling, 1)),
		Bounds:  AllocBounds{Ceiling: map[WorkType]int{WorkConstruction: 2}, Restricted: map[WorkType]bool{WorkHauling: true}},
	})
	checkDistinct(t, r)
	reasons := map[string]int{}
	for _, u := range r.Unfilled {
		reasons[u.Reason]++
	}
	if len(r.Assignments) != 2 || reasons[AllocReasonCeiling] != 1 || reasons[AllocReasonRestricted] != 1 {
		t.Fatalf("report = %+v", r)
	}
}

func TestAllocateStableAcrossInputOrder(t *testing.T) {
	ws := []AllocWorker{allocWorker("c", WorkCooking), allocWorker("a", WorkCooking, WorkConstruction), allocWorker("b", WorkCooking)}
	ready := allocReady(allocCand("cook", WorkCooking, 2), allocCand("build", WorkConstruction, 1))
	want := AllocateWorkers(AllocRequest{Workers: ws, Ready: ready})
	rev := []AllocWorker{ws[2], ws[0], ws[1]}
	if got := AllocateWorkers(AllocRequest{Workers: rev, Ready: ready}); !reflect.DeepEqual(got, want) {
		t.Fatalf("order dependent:\n%+v\n%+v", got, want)
	}
}

func TestAllocateAddingWorkerOnlyAddsFilled(t *testing.T) {
	ready := allocReady(allocCand("cook", WorkCooking, 1), allocCand("build", WorkConstruction, 2), allocCand("haul", WorkHauling, 1))
	ws := []AllocWorker{allocWorker("a", WorkCooking, WorkConstruction)}
	filled := func(r AllocationReport) map[string]bool {
		out := map[string]bool{}
		for _, a := range r.Assignments {
			out[fmt.Sprintf("%s/%d", a.Work, a.Position)] = true
		}
		return out
	}
	prev := filled(AllocateWorkers(AllocRequest{Workers: ws, Ready: ready}))
	for _, add := range []AllocWorker{allocWorker("b", WorkCooking), allocWorker("c", WorkHauling, WorkConstruction), allocWorker("d", WorkConstruction)} {
		ws = append(ws, add)
		r := AllocateWorkers(AllocRequest{Workers: ws, Ready: ready})
		checkDistinct(t, r)
		next := filled(r)
		for k := range prev {
			if !next[k] {
				t.Fatalf("adding %s unfilled %s", add.ID, k)
			}
		}
		prev = next
	}
	if len(prev) != 4 {
		t.Fatalf("filled = %v", prev)
	}
}

func TestAllocateBudgetContinues(t *testing.T) {
	var ws []AllocWorker
	for i := 0; i < 4; i++ {
		ws = append(ws, allocWorker(fmt.Sprintf("p%d", i), WorkCooking))
	}
	r := AllocateWorkers(AllocRequest{Workers: ws, Ready: allocReady(allocCand("cook", WorkCooking, 4)), Bounds: AllocBounds{MaxOps: 3}})
	if r.Continuation == "" || r.Unfilled[len(r.Unfilled)-1].Reason != AllocReasonBudget {
		t.Fatalf("report = %+v", r)
	}
}

func TestAllocateConservativeProfile(t *testing.T) {
	c := ReadyWork{ID: "legacy", Work: LaborProfile{WorkMining, WorkCrafting}, State: ReadyRunnable, Parallelism: 1, Adapter: ReadyConservative}
	r := AllocateWorkers(AllocRequest{Workers: []AllocWorker{allocWorker("a", WorkCrafting)}, Ready: allocReady(c)})
	if len(r.Assignments) != 1 {
		t.Fatalf("report = %+v", r)
	}
}

func BenchmarkAllocateAtBounds(b *testing.B) {
	types := []WorkType{WorkConstruction, WorkCooking, WorkGrowing, WorkHauling, WorkMining, WorkPlantCutting, WorkCrafting, WorkResearch}
	var ws []AllocWorker
	for i := 0; i < MaxAllocWorkers; i++ {
		ws = append(ws, allocWorker(fmt.Sprintf("p%02d", i), types[i%8], types[(i*3+1)%8], types[(i*5+2)%8]))
	}
	var cs []ReadyWork
	for i := 0; i < MaxAllocPositions/maxReadyParallelism; i++ {
		cs = append(cs, allocCand(fmt.Sprintf("c%02d", i), types[i%8], maxReadyParallelism))
	}
	req := AllocRequest{Workers: ws, Ready: allocReady(cs...)}
	var r AllocationReport
	for i := 0; i < b.N; i++ {
		r = AllocateWorkers(req)
	}
	b.ReportMetric(float64(len(r.Assignments)), "assignments")
	b.ReportMetric(float64(r.Ops), "ops")
	if r.Ops > MaxAllocOps || r.Continuation != "" {
		b.Fatalf("ops %d over contract", r.Ops)
	}
}

// A released worker's positions go unfilled; the workers left keep what
// they are serving.
func TestAllocateRemovingWorkerOnlyUnfillsItsPositions(t *testing.T) {
	ready := allocReady(allocCand("cook", WorkCooking, 1), allocCand("build", WorkConstruction, 2), allocCand("haul", WorkHauling, 1))
	all := []AllocWorker{allocWorker("a", WorkCooking, WorkConstruction), allocWorker("b", WorkCooking), allocWorker("c", WorkHauling, WorkConstruction), allocWorker("d", WorkConstruction)}
	full := assigned(AllocateWorkers(AllocRequest{Workers: all, Ready: ready}))
	if len(full) != 4 {
		t.Fatalf("full = %v", full)
	}
	for _, gone := range all {
		var rest []AllocWorker
		for _, w := range all {
			if w.ID != gone.ID {
				w.Occupancy, w.Serving = OccupancyServing, full[w.ID]
				rest = append(rest, w)
			}
		}
		r := AllocateWorkers(AllocRequest{Workers: rest, Ready: ready})
		checkDistinct(t, r)
		next := assigned(r)
		for pawn, work := range full {
			if pawn != gone.ID && next[pawn] != work {
				t.Fatalf("removing %s moved %s off %s: %v", gone.ID, pawn, work, next)
			}
		}
		if len(next) != len(full)-1 {
			t.Fatalf("removing %s: assigned %v, want %d", gone.ID, next, len(full)-1)
		}
	}
}
