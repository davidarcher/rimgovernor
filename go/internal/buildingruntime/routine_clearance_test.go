package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

type routineClearanceNative struct {
	*routineBlightNative
	rows []*o.ClearanceTarget
}

func (n *routineClearanceNative) ReadClearanceTargets(_ context.Context, _ *c.Identity) (*o.ClearanceTargetsReply, bridge.Result, error) {
	count := uint64(len(n.rows))
	return &o.ClearanceTargetsReply{Outcome: &o.ClearanceTargetsReply_Observed{Observed: &o.ClearanceTargetsSnapshot{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Targets: n.rows, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(count), Returned: proto.Uint64(count), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}, bridge.Result{}, nil
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
