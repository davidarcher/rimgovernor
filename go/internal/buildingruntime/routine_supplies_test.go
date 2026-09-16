package buildingruntime

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
	"testing"
)

type routineSupplyNative struct {
	context *c.ObservationContext
	onRead  func()
	cells   []domain.Cell
}

func (n *routineSupplyNative) ReadAllowSupplies(_ context.Context, _ *c.Identity, cell domain.Cell) (bridge.SupplyRead, bridge.Result, error) {
	n.cells = append(n.cells, cell)
	if n.onRead != nil {
		n.onRead()
	}
	out := bridge.SupplyRead{Context: n.context}
	for i := 0; i < 10; i++ {
		s, _ := domain.NewSupplyAllow(fmt.Sprintf("item-%02d", i), "Steel", cell)
		out.Targets = append(out.Targets, bridge.SupplyTarget{Supply: s, Token: "token"})
	}
	return out, bridge.Result{}, nil
}
func TestSupplyPlannerBoundsPendingWorkAndManualCancels(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	reviewer, db, session, request, native := routineFixture(t)
	native.reply.GetObserved().ForbiddenSupplies = []*c.Cell{{X: proto.Int32(1), Z: proto.Int32(2)}}
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	source := &routineSupplyNative{context: proto.Clone(native.reply.GetObserved().Context).(*c.ObservationContext)}
	planner, err := NewRoutineSupplyPlanner(reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	before := session.acquires.Load()
	result, err := planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 8 || session.acquires.Load() != before {
		t.Fatal(plan, err)
	}
	for _, p := range plan.Progress {
		if p.View().Stage != domain.Pending || p.View().Attempt != 0 {
			t.Fatal("planner dispatched", p)
		}
	}
	root := session.State().Snapshot
	target := root
	target.Plan = plan.Spec.ID()
	target.Revision = plan.Spec.Revision()
	if err = db.AuthorizeRoutinePlan(ctx, root, target); err != nil {
		t.Fatal(err)
	}
	if work, _, err := clockSchedulerWork(plan, target); err != nil || work {
		t.Fatal("Allow required game ticks", work, err)
	}
	if next, err := planner.Step(ctx); err != nil || next.Reason != BuildingMethodExistingWork || len(source.cells) != 1 {
		t.Fatal(next, err)
	}
	request.Kind, request.RequestID = store.PauseControl, "manual-supplies"
	if _, err = reviewer.player.Pause(ctx, request); err != nil {
		t.Fatal(err)
	}
	if next, err := planner.Step(ctx); err != nil || next.Reason != BuildingMethodDisabled {
		t.Fatal(next, err)
	}
	plan, err = db.LoadPlan(ctx, result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	for _, p := range plan.Progress {
		if p.View().Stage != domain.Cancelled {
			t.Fatal(p)
		}
	}
}
func TestSupplyPlannerRejectsChangedWorld(t *testing.T) {
	t.Parallel()
	reviewer, db, _, _, native := routineFixture(t)
	native.reply.GetObserved().ForbiddenSupplies = []*c.Cell{{X: proto.Int32(1), Z: proto.Int32(2)}}
	if _, err := reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	source := &routineSupplyNative{context: proto.Clone(native.reply.GetObserved().Context).(*c.ObservationContext)}
	source.context.Identity.LoadToken = proto.String("other")
	planner, _ := NewRoutineSupplyPlanner(reviewer, source)
	if _, err := planner.Step(context.Background()); err == nil {
		t.Fatal("accepted foreign census")
	}
	claims, err := db.SupplyClaims(context.Background(), playerWorld(reviewer.player.State().Snapshot))
	if err != nil || len(claims) != 0 {
		t.Fatal(claims, err)
	}
}
