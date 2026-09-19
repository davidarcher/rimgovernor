package buildingruntime

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

type routineClearanceNative struct {
	*routineBlightNative
	rows     []*o.ClearanceTarget
	chunks   []*o.ClearanceChunk
	sites    []*c.Cell
	previews []bridge.ZoneTarget
}

func (n *routineClearanceNative) ReadClearanceTargets(_ context.Context, _ *c.Identity) (*o.ClearanceTargetsReply, bridge.Result, error) {
	count := uint64(len(n.rows))
	return &o.ClearanceTargetsReply{Outcome: &o.ClearanceTargetsReply_Observed{Observed: &o.ClearanceTargetsSnapshot{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Targets: n.rows, Chunks: n.chunks, DumpSites: n.sites, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(count), Returned: proto.Uint64(count), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}, bridge.Result{}, nil
}
func (n *routineClearanceNative) PreviewZone(_ context.Context, _ *c.Identity, target bridge.ZoneTarget) (*op.PreviewReply, bridge.Result, error) {
	n.previews = append(n.previews, target)
	return &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Accepted: proto.Bool(true)}}}, bridge.Result{}, nil
}
func clearanceChunk(id string, x, z int32, forbidden, stored, destination bool) *o.ClearanceChunk {
	return &o.ClearanceChunk{EntityId: proto.String(id), DefName: proto.String("ChunkGranite"), Cell: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}, Forbidden: proto.Bool(forbidden), Stored: proto.Bool(stored), Destination: proto.Bool(destination)}
}
func clearanceTestRow(id string, x int32) *o.ClearanceTarget {
	return &o.ClearanceTarget{EntityId: proto.String(id), DefName: proto.String("Wall"), Occupied: &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(x), Z: proto.Int32(5)}, Maximum: &c.Cell{X: proto.Int32(x), Z: proto.Int32(5)}}, Class: o.ClearanceClass_CLEARANCE_CLASS_ANCIENT_WALL_DOOR, Deconstructible: proto.Bool(true), InHome: proto.Bool(true), AncientDanger: proto.Bool(false), Designated: proto.Bool(false), ControllerOwned: proto.Bool(false)}
}
func TestRoutineClearanceAdmitsNearestSingleTargetAndJournalsHolds(t *testing.T) {
	reviewer, db, _, _, native := routineFixture(t)
	v := native.reply.GetObserved()
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	newRow := func(id string) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	}
	native.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{newRow("cutter"), newRow("cutter2")}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(2), Returned: proto.Uint64(2), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}

	source := &routineClearanceNative{routineBlightNative: &routineBlightNative{routineNative: native}}
	source.rows = []*o.ClearanceTarget{clearanceTestRow("far", 80), clearanceTestRow("near", v.Center.GetX())}
	source.rows[1].Occupied.Minimum.Z = proto.Int32(v.Center.GetZ())
	source.rows[1].Occupied.Maximum.Z = proto.Int32(v.Center.GetZ())
	roof := clearanceTestRow("roof", v.Center.GetX())
	roof.RoofBlocker = proto.String("unsafe")
	source.rows = append(source.rows, roof)
	reviewer.native = source
	reviewer.methods = domain.Known([]policy.GoalID{policy.ClearHomeObstructions})
	ctx := context.Background()
	review, err := reviewer.Step(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if len(review.Review.ClearanceHolds) != 1 || review.Review.ClearanceHolds[0].Reason != "roof_blocker" {
		t.Fatal(review.Review.ClearanceHolds)
	}
	planner, err := NewRoutineClearancePlanner(reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err, review.Review.Development.Rows)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 1 {
		t.Fatal(plan, err)
	}
	target, ok := plan.Spec.Actions()[0].Deconstruction()
	if !ok || target.Target() != "near" {
		t.Fatal(target, ok)
	}
	root := reviewer.player.session.State().Snapshot
	scope := root
	scope.Plan = plan.Spec.ID()
	scope.Revision = plan.Spec.Revision()
	if err = db.AuthorizeRoutinePlan(ctx, root, scope); err != nil {
		t.Fatal(err)
	}
	if work, _, err := clockSchedulerWork(plan, scope); err != nil || !work {
		t.Fatal(work, err)
	}
	if next, err := planner.Step(ctx); err != nil || next.Reason != BuildingMethodExistingWork {
		t.Fatal(next, err)
	}
}

// Chunks are hauls: with no deconstructible target and a pending chunk (in
// Home, allowed, unstored, no store will take it), the planner admits one
// low-priority dumping stockpile on the native outdoor footprint sized for
// the pending stacks; a chunk ordinary hauling already has a destination for
// is no deficit at all.
func TestRoutineClearanceAdmitsChunkDumpForPendingChunks(t *testing.T) {
	reviewer, db, _, _, native := routineFixture(t)
	v := native.reply.GetObserved()
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	newRow := func(id string) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	}
	native.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{newRow("cutter"), newRow("cutter2")}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(2), Returned: proto.Uint64(2), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}
	v.Planning.GetObserved().ZoneMapSnapshot = &o.SnapshotRef{Context: proto.Clone(v.Context).(*c.ObservationContext), EntityId: proto.String(fmt.Sprintf("map-%d", v.Context.Identity.GetMapId())), Token: proto.String("zone-map")}

	source := &routineClearanceNative{routineBlightNative: &routineBlightNative{routineNative: native}}
	source.chunks = []*o.ClearanceChunk{clearanceChunk("hauled", 40, 40, false, false, true), clearanceChunk("stored", 41, 40, false, true, false)}
	reviewer.native = source
	reviewer.methods = domain.Known([]policy.GoalID{policy.ClearHomeObstructions})
	ctx := context.Background()
	review, err := reviewer.Step(ctx)
	if err != nil {
		t.Fatal(err)
	}
	for _, binding := range review.Review.Goals {
		if binding.Need != policy.ClearHomeObstructions {
			continue
		}
		if goal, err := db.LoadGoal(ctx, binding.Goal); err != nil || goal.Goal.Need == domain.NeedDeficit {
			t.Fatal("hauled and stored chunks are no clearance deficit", goal, err)
		}
	}

	source.chunks = append(source.chunks, clearanceChunk("loose", 42, 40, false, false, false), clearanceChunk("banned", 43, 40, true, false, false))
	for x := int32(50); x < 60; x++ {
		source.sites = append(source.sites, &c.Cell{X: proto.Int32(x), Z: proto.Int32(60)})
	}
	if review, err = reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineClearancePlanner(reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err, review.Review.Development.Rows)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 1 {
		t.Fatal(plan, err)
	}
	zone, ok := plan.Spec.Actions()[0].ZoneCreate()
	if !ok || zone.Priority() != domain.LowPriority || zone.Label() != "RimGovernor dumping" || len(zone.Cells()) != 4 || zone.Cells()[0] != (domain.Cell{X: 50, Z: 60}) {
		t.Fatal(zone, ok)
	}
	if allow := zone.Allow(); len(allow) != 2 || allow[0] != "ChunkGranite" || allow[1] != "ChunkSlagSteel" {
		t.Fatal(allow)
	}
	if len(source.previews) != 1 || source.previews[0].Token != "zone-map" {
		t.Fatal(source.previews)
	}
	if next, err := planner.Step(ctx); err != nil || next.Reason != BuildingMethodExistingWork {
		t.Fatal(next, err)
	}
}
