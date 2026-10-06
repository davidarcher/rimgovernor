package buildingruntime

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	pl "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	"google.golang.org/protobuf/proto"
)

// coverLootCell stands a built wall and its Home coverage at cell, so the
// derived colony extent covers it and the reach filter holds nothing there.
func coverLootCell(n *roundsNative, cell domain.Cell) {
	v := n.reply.GetObserved()
	if n.built == nil {
		n.built = map[domain.ActionID]*o.BuildingState{}
	}
	at := &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)}
	id := fmt.Sprintf("cover-%d-%d", cell.X, cell.Z)
	n.built[domain.ActionID(id)] = &o.BuildingState{
		Building: &o.EntityRef{Id: proto.String(id), DefName: proto.String("Wall"), MapId: v.Context.Identity.MapId, Position: at},
		Occupied: &o.Rectangle{Minimum: at, Maximum: at},
		Status:   o.BuildingStatus_BUILDING_STATUS_BUILT.Enum(),
		Rotation: pl.Rotation_ROTATION_NORTH.Enum(),
		Stuff:    proto.String("WoodLog"),
	}
	if v.Upkeep.GetObserved() == nil {
		v.Upkeep = &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: &o.UpkeepFacts{Comfort: &o.ComfortSection{Outcome: &o.ComfortSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_REQUESTED.Enum()}}}}}}
	}
	coverage := v.Upkeep.GetObserved().GetHomeCoverage().GetObserved()
	if coverage == nil {
		coverage = &o.HomeCoverageFacts{Revision: proto.Int64(1)}
		v.Upkeep.GetObserved().HomeCoverage = &o.HomeCoverageSection{Outcome: &o.HomeCoverageSection_Observed{Observed: coverage}}
	}
	coverage.Targets = append(coverage.Targets, &o.HomeCoverageTarget{Id: proto.String(id), ShapeToken: proto.String("shape"), MissingCells: proto.Uint32(0), ExcludedCells: proto.Uint32(0), Cells: []*c.Cell{at}, ExtentGeometry: &o.HomeExtentGeometry{}})
}

// forbiddenSupplyRows is the loot census of count safe forbidden Steel stacks
// at cell, the stacks ManageSupplySafety releases.
func forbiddenSupplyRows(n *roundsNative, count int, cell domain.Cell) *o.LootSection {
	coverLootCell(n, cell)
	census := &o.LootCensus{FreeHaulers: proto.Int64(5), StorytellerQuiet: proto.Bool(true)}
	for i := 0; i < count; i++ {
		ref := n.thing(&o.Thing{Thing: &o.EntityRef{Id: proto.String(fmt.Sprintf("item-%02d", i)), DefName: proto.String("Steel"), MapId: n.reply.GetObserved().Context.Identity.MapId, Position: &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)}}})
		census.Items = append(census.Items, &o.LootItem{Item: ref, Forbidden: proto.Bool(true), SafeToHaul: proto.Bool(true), Count: proto.Int64(10), PathLength: proto.Float64(1), StorageHeadroom: proto.Int64(100)})
	}
	return &o.LootSection{Outcome: &o.LootSection_Observed{Observed: census}}
}

type roundsSupplyNative struct {
	context *c.ObservationContext
	onRead  func()
	cells   []domain.Cell
}

func (n *roundsSupplyNative) ReadAllowSupplies(_ context.Context, _ *c.Identity, cell domain.Cell) (bridge.SupplyRead, bridge.Result, error) {
	n.cells = append(n.cells, cell)
	if n.onRead != nil {
		n.onRead()
	}
	out := bridge.SupplyRead{Context: n.context}
	for i := 0; i < 10; i++ {
		s, _ := domain.NewSupplyAllow(fmt.Sprintf("item-%02d", i), "Steel", cell)
		out.Targets = append(out.Targets, bridge.SupplyTarget{Supply: s})
	}
	return out, bridge.Result{}, nil
}
func TestSupplyPlannerBoundsPendingWorkAndManualCancels(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	ctx := context.Background()
	reviewer, db, session, request, native := roundsFixture(t)
	native.reply.GetObserved().EventLoot = forbiddenSupplyRows(native, 10, domain.Cell{X: 1, Z: 2})
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	source := &roundsSupplyNative{context: proto.Clone(native.reply.GetObserved().Context).(*c.ObservationContext)}
	planner, err := NewRoundsSupplyPlanner(reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	before := session.acquires.Load()
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
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
	if err = db.AuthorizeRoundsPlan(ctx, root, target); err != nil {
		t.Fatal(err)
	}
	if work, _, err := clockSchedulerWork(plan, target); err != nil || work {
		t.Fatal("Allow required game ticks", work, err)
	}
	if next, err := planner.Step(ctx); err != nil || next.Verdict != BuildingReasonExistingWork || len(source.cells) != 1 {
		t.Fatal(next, err)
	}
	request.Kind, request.RequestID = store.PauseControl, "manual-supplies"
	if _, err = reviewer.player.Pause(ctx, request); err != nil {
		t.Fatal(err)
	}
	if next, err := planner.Step(ctx); err != nil || next.Verdict != BuildingReasonDisabled {
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
	reviewer, _, _, _, native := roundsFixture(t)
	native.reply.GetObserved().EventLoot = forbiddenSupplyRows(native, 1, domain.Cell{X: 1, Z: 2})
	if _, err := reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	source := &roundsSupplyNative{context: proto.Clone(native.reply.GetObserved().Context).(*c.ObservationContext)}
	source.context.Identity.LoadToken = proto.String("other")
	planner, _ := NewRoundsSupplyPlanner(reviewer, source)
	if _, err := planner.Step(context.Background()); err == nil {
		t.Fatal("accepted foreign census")
	}
}

// A stack the census lists is targeted only at the cell the census reported
// and only when it is still in the cohort: the native read's extra items at
// that cell are later forbids, and a stack hauled aside is re-read at its
// new cell on the next review.
func TestSupplyPlannerTargetsCohortStacksAtTheirCensusCell(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	ctx := context.Background()
	reviewer, db, _, _, native := roundsFixture(t)
	first, moved := domain.Cell{X: 1, Z: 2}, domain.Cell{X: 4, Z: 4}
	native.reply.GetObserved().EventLoot = forbiddenSupplyRows(native, 2, first)
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	source := &roundsSupplyNative{context: proto.Clone(native.reply.GetObserved().Context).(*c.ObservationContext)}
	planner, err := NewRoundsSupplyPlanner(reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
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
	native.reply.GetObserved().EventLoot = forbiddenSupplyRows(native, 2, moved)
	native.reply.GetObserved().EventLoot.GetObserved().Items = native.reply.GetObserved().EventLoot.GetObserved().Items[1:]
	if _, err = reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	source.cells = nil
	result, err = planner.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
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
