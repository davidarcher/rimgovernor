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
	foodPlanFixture(v)
	issues := v.Issues[:0]
	for _, issue := range v.Issues {
		if issue.GetField() != "cooking" {
			issues = append(issues, issue)
		}
	}
	v.Issues = issues
	native.catalog[0].Definition.DefName = proto.String("Campfire")
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
	if err != nil || len(plan.Progress) != 1 || len(plan.Admissions) != 0 {
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
	for _, change := range []string{"usable", "campfire", "unknown", "definition"} {
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
			case "definition":
				want = BuildingMethodUnknown
				native.catalog[0].ResearchPrerequisites = []string{"Unfinished"}
			}
			result, err := p.Step(context.Background())
			if err != nil || result.Reason != want || result.Decision.Admitted {
				t.Fatal(result, err)
			}
		})
	}
}

// A campfire the pawns let burn out leaves the census without a cooking
// bench in the same goal epoch; the completed method yields to a numbered
// successor instead of holding the goal at method_already_used (#217).
func TestRoutineCookingRestagesBurntOutCampfire(t *testing.T) {
	t.Parallel()
	p, db, native := cookingFixture(t)
	ctx := context.Background()
	result, err := p.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	completeRoutineBuildingMethod(t, db, result)
	// The census still reports no cooking bench: the campfire burnt out.
	// Its intent is gone from a known census too (#856).
	native.built = map[domain.ActionID]*o.BuildingState{}
	if _, err := p.reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	again, err := p.Step(ctx)
	if err != nil || again.Reason != BuildingMethodAdmitted || native.previews != 2 {
		t.Fatal(again, err, native.previews)
	}
	// The burnt-out campfire's plan retired on the census (#856); the
	// goal's history still binds both methods.
	goal := again.Decision.Goal.Goal
	methods, err := db.LoadGoalMethods(ctx, goal.ID, goal.Epoch)
	if err != nil || len(methods) != 2 || methods[0].Method != "campfire" || methods[1].Method != "campfire-1" {
		t.Fatal(methods, err)
	}
	if next, err := p.Step(ctx); err != nil || next.Reason != BuildingMethodExistingWork {
		t.Fatal(next, err)
	}
}
