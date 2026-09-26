package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A shelter shell admitted short of wood records a typed edge; the next
// review orders MaintainWood by it while the shortfall stays open, and
// drops the edge when the shell's actions settle (#651).
func TestShelterShortfallDonatesUntilSatisfied(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := routineRequest()
	r.Facts.Wood = domain.Known(int64(40))
	r.Facts.Colonists, r.Facts.IndoorCapacity, r.Facts.BedCapacity = domain.Known(int64(3)), domain.Known(int64(0)), domain.Known(int64(0))
	r.Facts.Labor = domain.Known(map[policy.WorkType]int{policy.WorkPlantCutting: 1, policy.WorkConstruction: 1})
	first := reviewRoutine(t, s, &r)
	shelter := routineGoal(t, first, policy.EnsureInitialShelter)
	if shelter.Goal.Status != domain.GoalActive {
		t.Fatalf("shelter %+v", shelter.Goal)
	}
	req := methodRequest(t, shelter, "shell", 60, 60)
	req.Purpose = policy.Shelter
	req.Stock.Values = []policy.Stock{{Resource: "WoodLog", Available: domain.Known(int64(40))}}
	d, err := s.AdmitBuildingMethod(ctx, req)
	if err != nil || !d.Admitted {
		t.Fatalf("shell %+v %v", d, err)
	}
	rec, short := ShortfallDependency(policy.EnsureInitialShelter, d.Goal.Goal, req.Method, req.Plan.ID(), req.Previews, req.Stock, "WoodLog", req.Tick)
	if !short || len(rec.Costs) != 2 {
		t.Fatalf("record %+v", rec)
	}
	if err = s.RecordDependency(ctx, first.Review.Revision, rec); err != nil {
		t.Fatal(err)
	}
	second := reviewRoutine(t, s, &r)
	wood := developmentRow(t, second.Review, policy.MaintainWood)
	if wood.Donation == nil || wood.Donation.Priority != 2 || wood.Donation.Shortfall != 80 || len(second.Review.Dependencies) != 1 {
		t.Fatalf("wood row %+v, deps %+v", wood, second.Review.Dependencies)
	}
	// Stock covers the open costs: no donation, the edge stays while the
	// frames are open.
	r.Facts.Wood = domain.Known(int64(150))
	third := reviewRoutine(t, s, &r)
	if w := developmentRow(t, third.Review, policy.MaintainWood); w.Donation != nil || len(third.Review.Dependencies) != 1 {
		t.Fatalf("covered: %+v", w)
	}
	// Cancelled actions settle the dependency: the record drops.
	r.Facts.Wood = domain.Known(int64(40))
	for _, a := range req.Plan.Actions() {
		if _, err = s.Cancel(ctx, req.Plan.ID(), a.ID()); err != nil {
			t.Fatal(err)
		}
	}
	fourth := reviewRoutine(t, s, &r)
	if w := developmentRow(t, fourth.Review, policy.MaintainWood); w.Donation != nil || len(fourth.Review.Dependencies) != 0 {
		t.Fatalf("settled: %+v %+v", w, fourth.Review.Dependencies)
	}
}

func TestShortfallDependencyNeedsMeasuredShortfall(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	defer s.Close()
	g := anotherGoal(t, s, "shelter")
	req := methodRequest(t, g, "shell", 60)
	if _, short := ShortfallDependency(policy.EnsureInitialShelter, g.Goal, "shell", req.Plan.ID(), req.Previews, req.Stock, "WoodLog", 10); short {
		t.Fatal("covered stock recorded a shortfall")
	}
	req.Stock.Values = []policy.Stock{{Resource: "WoodLog", Available: domain.Unknown[int64]()}}
	if _, short := ShortfallDependency(policy.EnsureInitialShelter, g.Goal, "shell", req.Plan.ID(), req.Previews, req.Stock, "WoodLog", 10); short {
		t.Fatal("unknown stock recorded a shortfall")
	}
	if _, short := ShortfallDependency(policy.EnsureInitialShelter, g.Goal, "shell", req.Plan.ID(), req.Previews, policy.StockObservation{Values: []policy.Stock{{Resource: "Steel", Available: domain.Known(int64(0))}}}, "Steel", 10); short {
		t.Fatal("a resource the previews do not cost recorded a shortfall")
	}
}

// A shell short of a non-wood material records an edge to MaintainResource;
// the review raises that resource's floor to the open costs and carries it
// for the resource planner (#728).
func TestShelterNonWoodShortfallRaisesResourceFloor(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := routineRequest()
	r.Facts.Wood = domain.Known(int64(400))
	r.Facts.Resources = domain.Known([]policy.Amount{{Resource: "BlocksGranite", Count: 30}})
	r.Facts.Colonists, r.Facts.IndoorCapacity, r.Facts.BedCapacity = domain.Known(int64(3)), domain.Known(int64(0)), domain.Known(int64(0))
	r.Facts.Labor = domain.Known(map[policy.WorkType]int{policy.WorkPlantCutting: 1, policy.WorkConstruction: 1})
	first := reviewRoutine(t, s, &r)
	shelter := routineGoal(t, first, policy.EnsureInitialShelter)
	req := methodRequest(t, shelter, "shell", 60, 60)
	req.Purpose = policy.Shelter
	for i := range req.Previews {
		req.Previews[i].Costs = domain.Known([]policy.Amount{{Resource: "BlocksGranite", Count: 60}})
	}
	req.Stock.Values = []policy.Stock{{Resource: "BlocksGranite", Available: domain.Known(int64(30))}}
	rec, short := ShortfallDependency(policy.EnsureInitialShelter, shelter.Goal, req.Method, req.Plan.ID(), req.Previews, req.Stock, "BlocksGranite", req.Tick)
	if !short {
		t.Fatal("granite shortfall not recorded")
	}
	if _, err := s.AdmitBuildingMethod(ctx, req); err != nil {
		t.Fatal(err)
	}
	if err := s.RecordDependency(ctx, first.Review.Revision, rec); err != nil {
		t.Fatal(err)
	}
	second := reviewRoutine(t, s, &r)
	if got := second.Review.DependencyNeeds["BlocksGranite"]; got != 120 {
		t.Fatalf("floor %d, deps %+v", got, second.Review.Dependencies)
	}
	row := developmentRow(t, second.Review, policy.MaintainResource)
	if row.Donation == nil || row.Donation.Priority != 2 || row.Donation.Shortfall != 90 {
		t.Fatalf("resource row %+v", row)
	}
}
