package buildingruntime

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

// forbiddenSupplyRows seeds count forbidden starting stacks item-00.. at one cell.
func forbiddenSupplyRows(n *routineNative, count int, cell domain.Cell) []*o.EntityRef {
	var rows []*o.EntityRef
	for i := 0; i < count; i++ {
		rows = append(rows, &o.EntityRef{Id: proto.String(fmt.Sprintf("item-%02d", i)), DefName: proto.String("Steel"), MapId: n.reply.GetObserved().Context.Identity.MapId, Position: &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)}})
	}
	return rows
}

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
	native.reply.GetObserved().ForbiddenSupplies = forbiddenSupplyRows(native, 10, domain.Cell{X: 1, Z: 2})
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
	// Pause suspends the goal; the pending method waits for the resume.
	for _, p := range plan.Progress {
		if p.View().Stage != domain.Pending {
			t.Fatal(p)
		}
	}
}
func TestSupplyPlannerRejectsChangedWorld(t *testing.T) {
	t.Parallel()
	reviewer, db, _, _, native := routineFixture(t)
	native.reply.GetObserved().ForbiddenSupplies = forbiddenSupplyRows(native, 1, domain.Cell{X: 1, Z: 2})
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

// A stack the census lists is targeted only at the cell the census reported
// and only when it is still in the cohort: the native read's extra items at
// that cell are later forbids, and a stack hauled aside is re-read at its
// new cell on the next review.
func TestSupplyPlannerTargetsCohortStacksAtTheirCensusCell(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	reviewer, db, _, _, native := routineFixture(t)
	first, moved := domain.Cell{X: 1, Z: 2}, domain.Cell{X: 4, Z: 4}
	native.reply.GetObserved().ForbiddenSupplies = forbiddenSupplyRows(native, 2, first)
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	source := &routineSupplyNative{context: proto.Clone(native.reply.GetObserved().Context).(*c.ObservationContext)}
	planner, err := NewRoutineSupplyPlanner(reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 2 || len(source.cells) != 1 {
		t.Fatal(plan, err, source.cells)
	}
	for _, p := range plan.Progress {
		supply, _ := p.Action().SupplyAllow()
		if supply.Cell() != first || supply.Thing() != "item-00" && supply.Thing() != "item-01" {
			t.Fatal("targeted a later forbid", supply)
		}
	}
	// item-01 was hauled aside before its Allow: the plan settles and the
	// next census reports it at the new cell, where the planner re-reads it.
	for _, p := range plan.Progress {
		if _, err = db.Cancel(ctx, plan.Spec.ID(), p.Action().ID()); err != nil {
			t.Fatal(err)
		}
	}
	native.reply.GetObserved().ForbiddenSupplies = forbiddenSupplyRows(native, 2, moved)[1:]
	if _, err = reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	source.cells = nil
	result, err = planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	plan, err = db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 1 || len(source.cells) != 1 || source.cells[0] != moved {
		t.Fatal(plan, err, source.cells)
	}
	supply, _ := plan.Progress[0].Action().SupplyAllow()
	if supply.Thing() != "item-01" || supply.Cell() != moved {
		t.Fatal(supply)
	}
}
