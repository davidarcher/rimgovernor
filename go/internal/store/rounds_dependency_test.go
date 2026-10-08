package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A shelter shell admitted short of wood records its open costs; the next
// review admits MaintainResource and carries them while the shortfall stays open, and
// drops them when the shell's actions settle (#651).
func TestShelterShortfallAdmitsMaintainResourceUntilSatisfied(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := roundsRequest()
	r.Facts.Wood = domain.Known(int64(40))
	r.Facts.Colonists, r.Facts.IndoorCapacity, r.Facts.BedCapacity = domain.Known(int64(3)), domain.Known(int64(0)), domain.Known(int64(0))
	r.Facts.Labor = domain.Known(map[policy.WorkType]int{policy.WorkPlantCutting: 1, policy.WorkConstruction: 1})
	first := reviewRounds(t, s, &r)
	shelter := roundsGoal(t, first, policy.MaintainHousing)
	if shelter.Standard.Status != domain.StandardOpen {
		t.Fatalf("shelter %+v", shelter.Standard)
	}
	req := methodRequest(t, shelter, "shell", 60, 60)
	req.Purpose = policy.Shelter
	req.Stock.Values = []policy.Stock{{Resource: "WoodLog", Available: domain.Known(int64(40))}}
	d, err := s.AdmitBuildingMethod(ctx, req)
	if err != nil || !d.Admitted {
		t.Fatalf("shell %+v %v", d, err)
	}
	rec, short := ShortfallDependency(policy.MaintainHousing, d.Standard.Standard, req.Method, req.Plan.ID(), req.Previews, req.Stock, "WoodLog", req.Tick)
	if !short || len(rec.Costs) != 2 {
		t.Fatalf("record %+v", rec)
	}
	if err = s.RecordDependency(ctx, first.Review.Revision, rec); err != nil {
		t.Fatal(err)
	}
	second := reviewRounds(t, s, &r)
	if len(second.Review.Dependencies) != 1 || len(second.Detection.Facts.Admitted) != 2 {
		t.Fatalf("deps %+v", second.Review.Dependencies)
	}
	// Stock covers the open costs: no donation, the edge stays while the
	// frames are open.
	r.Facts.Wood = domain.Known(int64(150))
	third := reviewRounds(t, s, &r)
	if len(third.Review.Dependencies) != 1 {
		t.Fatalf("covered: %+v", third.Review.Dependencies)
	}
	// Cancelled actions settle the dependency: the record drops.
	r.Facts.Wood = domain.Known(int64(40))
	for _, a := range req.Plan.Actions() {
		if _, err = s.Cancel(ctx, req.Plan.ID(), a.ID()); err != nil {
			t.Fatal(err)
		}
	}
	fourth := reviewRounds(t, s, &r)
	if len(fourth.Review.Dependencies) != 0 || len(fourth.Detection.Facts.Admitted) != 0 {
		t.Fatalf("settled: %+v", fourth.Review.Dependencies)
	}
}

func TestShortfallDependencyNeedsMeasuredShortfall(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	defer s.Close()
	g := anotherGoal(t, s, "shelter")
	req := methodRequest(t, g, "shell", 60)
	if _, short := ShortfallDependency(policy.MaintainHousing, g.Standard, "shell", req.Plan.ID(), req.Previews, req.Stock, "WoodLog", 10); short {
		t.Fatal("covered stock recorded a shortfall")
	}
	req.Stock.Values = []policy.Stock{{Resource: "WoodLog", Available: domain.Unknown[int64]()}}
	if _, short := ShortfallDependency(policy.MaintainHousing, g.Standard, "shell", req.Plan.ID(), req.Previews, req.Stock, "WoodLog", 10); short {
		t.Fatal("unknown stock recorded a shortfall")
	}
	if _, short := ShortfallDependency(policy.MaintainHousing, g.Standard, "shell", req.Plan.ID(), req.Previews, policy.StockObservation{Values: []policy.Stock{{Resource: "Steel", Available: domain.Known(int64(0))}}}, "Steel", 10); short {
		t.Fatal("a resource the previews do not cost recorded a shortfall")
	}
}

// A shell short of a non-wood material records an edge to MaintainResource;
// the review raises that resource's floor to the open costs and carries it
// for the resource planner.
func TestShelterNonWoodShortfallRaisesResourceFloor(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := roundsRequest()
	r.Facts.Wood = domain.Known(int64(400))
	r.Facts.Resources = domain.Known([]policy.Amount{{Resource: "BlocksGranite", Count: 30}})
	r.Facts.Colonists, r.Facts.IndoorCapacity, r.Facts.BedCapacity = domain.Known(int64(3)), domain.Known(int64(0)), domain.Known(int64(0))
	r.Facts.Labor = domain.Known(map[policy.WorkType]int{policy.WorkPlantCutting: 1, policy.WorkConstruction: 1})
	first := reviewRounds(t, s, &r)
	shelter := roundsGoal(t, first, policy.MaintainHousing)
	req := methodRequest(t, shelter, "shell", 60, 60)
	req.Purpose = policy.Shelter
	for i := range req.Previews {
		req.Previews[i].Costs = domain.Known([]policy.Amount{{Resource: "BlocksGranite", Count: 60}})
	}
	req.Stock.Values = []policy.Stock{{Resource: "BlocksGranite", Available: domain.Known(int64(30))}}
	rec, short := ShortfallDependency(policy.MaintainHousing, shelter.Standard, req.Method, req.Plan.ID(), req.Previews, req.Stock, "BlocksGranite", req.Tick)
	if !short {
		t.Fatal("granite shortfall not recorded")
	}
	if _, err := s.AdmitBuildingMethod(ctx, req); err != nil {
		t.Fatal(err)
	}
	if err := s.RecordDependency(ctx, first.Review.Revision, rec); err != nil {
		t.Fatal(err)
	}
	second := reviewRounds(t, s, &r)
	if len(second.Review.Dependencies) != 1 || policy.ConstructionDemandOf(second.Detection.Facts, second.Detection.Policy, second.Review.Latches)["BlocksGranite"] < 120 {
		t.Fatal(second.Review.Dependencies, second.Detection.Facts.Admitted)
	}
}
