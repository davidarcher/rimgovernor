package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func cookingFixture(t *testing.T) (*RoutineBuildingPlanner, *store.Store, *sleepingNative) {
	sleeping, db, _, _, native := sleepingFixture(t)
	v := native.reply.GetObserved()
	issues := v.Issues[:0]
	for _, issue := range v.Issues {
		if issue.GetField() != "cooking" {
			issues = append(issues, issue)
		}
	}
	v.Issues = issues
	v.Planning.GetObserved().Definitions[0].Definition.DefName = proto.String("Campfire")
	if _, err := sleeping.reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineCookingPlanner(sleeping.reviewer, native)
	if err != nil {
		t.Fatal(err)
	}
	native.onPreview = func(_ context.Context, p *bridge.BuildingPreview) {
		p.Preview.Costs = domain.Known([]policy.Amount{{Resource: "WoodLog", Count: 5}})
		p.Stock.Values = []policy.Stock{{Resource: "WoodLog", Available: domain.Known(int64(5))}}
	}
	return planner, db, native
}

func TestRoutineCookingAdmitsSingleCostedMethodWithoutCertifyingFood(t *testing.T) {
	t.Parallel()
	p, db, native := cookingFixture(t)
	result, err := p.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted || native.previews != 1 {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(context.Background(), result.Decision.Goal.Methods[0].Plan)
	if err != nil || len(plan.Progress) != 1 || len(plan.Admissions) != 1 || plan.Admissions[0].Admission.Costs[0].Count != 5 {
		t.Fatal(plan, err)
	}
	b, _ := plan.Spec.Actions()[0].Building()
	if b.Definition() != "Campfire" || plan.Progress[0].View().Attempt != 0 || result.Decision.Goal.Goal.Need != domain.NeedDeficit {
		t.Fatal(plan, result)
	}
	if next, err := p.Step(context.Background()); err != nil || next.Reason != BuildingMethodExistingWork || native.previews != 1 {
		t.Fatal(next, err)
	}
}

func TestRoutineCookingWaitsForExistingFacilitiesAndUnknownInputs(t *testing.T) {
	t.Parallel()
	for _, change := range []string{"usable", "campfire", "unknown", "stock", "definition"} {
		t.Run(change, func(t *testing.T) {
			p, _, native := cookingFixture(t)
			v := native.reply.GetObserved()
			want := BuildingExistingFacility
			switch change {
			case "usable", "campfire", "unknown":
				bench := &o.CookingFacts{Bench: &o.EntityRef{Id: proto.String("bench"), DefName: proto.String("FueledStove"), MapId: proto.Int32(0), Position: &c.Cell{X: proto.Int32(1), Z: proto.Int32(1)}}, Usable: proto.Bool(true)}
				if change == "campfire" {
					bench.Bench.DefName = proto.String("Campfire")
					bench.Usable = proto.Bool(false)
				}
				if change == "unknown" {
					bench.Usable = nil
					want = BuildingMethodUnknown
				}
				v.Cooking = []*o.CookingFacts{bench}
			case "stock":
				want = BuildingMethodRefused
				old := native.onPreview
				native.onPreview = func(ctx context.Context, preview *bridge.BuildingPreview) {
					old(ctx, preview)
					preview.Stock.Values[0].Available = domain.Known(int64(4))
				}
			case "definition":
				want = BuildingMethodUnknown
				v.Planning.GetObserved().Definitions[0].Available = proto.Bool(false)
			}
			result, err := p.Step(context.Background())
			if err != nil || result.Reason != want || result.Decision.Admitted {
				t.Fatal(result, err)
			}
		})
	}
}

func TestRoutineCookingCannotSpendPlayerReservation(t *testing.T) {
	t.Parallel()
	p, db, _ := cookingFixture(t)
	ctx := context.Background()
	root := p.reviewer.player.State().Snapshot
	plan, err := db.LoadPlan(ctx, root.Plan)
	if err != nil {
		t.Fatal(err)
	}
	a := plan.Spec.Actions()[0]
	b, _ := a.Building()
	if _, err = db.ReserveAndPrepare(ctx, plan.Spec.ID(), a.ID(), store.Admission{Snapshot: root, Tick: 7, Costs: []store.MaterialCost{{Definition: "WoodLog", Count: 5}}, Footprint: []domain.Cell{b.Cell()}}); err != nil {
		t.Fatal(err)
	}
	result, err := p.Step(ctx)
	if err != nil || result.Reason != BuildingMethodRefused || len(result.Decision.Refused) != 1 || result.Decision.Refused[0].Reason != policy.InsufficientStock {
		t.Fatal(result, err)
	}
}

func TestRoutineCookingWaitsForOtherCommittedCampfire(t *testing.T) {
	t.Parallel()
	p, db, native := cookingFixture(t)
	ctx := context.Background()
	b, err := domain.NewBuilding("Campfire", domain.Cell{X: 30, Z: 30}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewBuildingAction("other-fire", b)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("other-cooking", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = db.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	scope := p.reviewer.player.State().Snapshot
	scope.Plan = plan.ID()
	if _, err = db.ReserveAndPrepare(ctx, plan.ID(), a.ID(), store.Admission{Snapshot: scope, Tick: 7, Costs: []store.MaterialCost{{Definition: "WoodLog", Count: 5}}, Footprint: []domain.Cell{b.Cell()}}); err != nil {
		t.Fatal(err)
	}
	result, err := p.Step(ctx)
	if err != nil || result.Reason != BuildingMethodExistingWork || native.previews != 0 {
		t.Fatal(result, err)
	}
}
