package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// stampContextTicks sets the tick of every ObservationContext inside a
// fake native reply, so a reply advanced a tick stays one census.
func stampContextTicks(m protoreflect.Message, tick int64) {
	if ctx, ok := m.Interface().(*c.ObservationContext); ok {
		ctx.Tick = proto.Int64(tick)
		return
	}
	m.Range(func(fd protoreflect.FieldDescriptor, v protoreflect.Value) bool {
		switch {
		case fd.IsList() && fd.Kind() == protoreflect.MessageKind:
			for i := 0; i < v.List().Len(); i++ {
				stampContextTicks(v.List().Get(i).Message(), tick)
			}
		case fd.IsMap():
		case fd.Kind() == protoreflect.MessageKind:
			stampContextTicks(v.Message(), tick)
		}
		return true
	})
}

// The colony stage gates a development proposal (#630): at Foothold with
// the shelter unmet the stone shell's ranking row reads stage_foothold and
// the planner's bundle is refused; once the colony climbs to Development
// on the same fake native the same proposal is admitted.
func TestRoutineStoneShellFollowsColonyStage(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	p, db, n := stoneShellFixture(t)
	v := n.reply.GetObserved()
	stage := func() policy.ColonyStageRecord {
		t.Helper()
		review, err := db.LoadRoutineReview(ctx)
		if err != nil || review.Stage == nil {
			t.Fatal(review.Revision, err)
		}
		return *review.Stage
	}
	row := func() store.RoutineDevelopmentRow {
		t.Helper()
		review, err := db.LoadRoutineReview(ctx)
		if err != nil {
			t.Fatal(err)
		}
		for _, row := range review.Development.Rows {
			if row.Goal == policy.MaintainStoneShell {
				return row
			}
		}
		t.Fatal("stone shell not ranked", review.Development.Rows)
		return store.RoutineDevelopmentRow{}
	}
	// No indoor sleeping slot for the one colonist: the shelter gate is
	// unmet and Foothold holds the comfort-class development.
	indoor := v.IndoorSleepingCapacity
	v.IndoorSleepingCapacity = proto.Uint32(0)
	if _, err := p.reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	if s := stage(); s.Stage != policy.StageFoothold || !s.Held || s.Blocker != policy.StageBlockerShelter {
		t.Fatalf("foothold stage %+v", s)
	}
	if r := row(); r.Selected || r.Reason != policy.DevelopmentStage {
		t.Fatalf("stone shell row %+v", r)
	}
	if result, err := p.Step(ctx); err != nil || result.Reason != BuildingMethodRefused {
		t.Fatal(result, err)
	}
	if n.previews != 1 {
		t.Fatal("bundle not proposed", n.previews)
	}
	// The colony climbs: shelter for everyone, a thirty-day food runway,
	// a wood stock over the floor and the medicine reserve take it to
	// Reserves; with the settling times cut to one tick each review after
	// that climbs one stage until Development.
	v.IndoorSleepingCapacity = indoor
	v.Resources = []*o.Quantity{{DefName: proto.String("WoodLog"), Units: proto.Int64(400)}, {DefName: proto.String("MedicineHerbal"), Units: proto.Int64(10)}}
	count := func(n uint64) *o.Completeness {
		return &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(n), Returned: proto.Uint64(n), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
	}
	food := &o.FoodSupplyFacts{Consumers: []*o.FoodConsumer{{PawnId: proto.String("builder"), NutritionPerDay: proto.Float64(1)}},
		Stocks: []*o.FoodStock{{Item: &o.EntityRef{Id: proto.String("pemmican"), DefName: proto.String("Pemmican")}, Count: proto.Int64(60), Nutrition: proto.Float64(30), Perishable: proto.Bool(false), EaterIds: []string{"builder"}}}, Completeness: count(2)}
	v.FoodSupply = &o.FoodSupplySection{Outcome: &o.FoodSupplySection_Observed{Observed: food}}
	v.Forecast = &o.ForecastSection{Outcome: &o.ForecastSection_Observed{Observed: &o.ForecastFacts{CombinedFoodSupply: proto.Clone(food).(*o.FoodSupplyFacts), Patients: []*o.PatientForecast{{PawnId: proto.String("builder")}}, Completeness: count(2)}}}
	p.reviewer.policy.Stage = policy.ColonyStagePolicy{StableTicks: 1, StableExitTicks: 1, DevelopmentTicks: 1}
	for _, want := range []policy.ColonyStage{policy.StageReserves, policy.StageStable, policy.StageDevelopment} {
		tick := v.Context.GetTick() + 1
		for _, m := range []proto.Message{n.reply, n.pawnReply, n.buildings, n.sites.Context} {
			stampContextTicks(m.ProtoReflect(), tick)
		}
		if _, err := p.reviewer.Step(ctx); err != nil {
			t.Fatal(err)
		}
		if s := stage(); s.Stage != want || s.Held {
			t.Fatalf("expected %s, got %+v", want, s)
		}
	}
	if s := stage(); s.Blocker != "" || s.Reason != "" {
		t.Fatalf("development stage %+v", s)
	}
	if r := row(); !r.Selected || r.Reason != "" {
		t.Fatalf("stone shell row at Development %+v", r)
	}
	result, err := p.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted || n.previews != 2 {
		t.Fatal(result, err, n.previews)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 2 {
		t.Fatal(plan, err)
	}
	if replacement, ok := plan.Spec.Actions()[1].Building(); !ok || replacement.Cell() != (domain.Cell{X: 4, Z: 4}) || replacement.Stuff() != "BlocksGranite" {
		t.Fatal(plan.Spec.Actions())
	}
}
