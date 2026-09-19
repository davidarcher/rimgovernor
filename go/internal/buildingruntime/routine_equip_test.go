package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// equipTestNative is a colony of unarmed colonists beside one loose bow.
type equipTestNative struct {
	*routineNative
	ids []string
}

func (n *equipTestNative) pawn(id string) *o.PawnState {
	v := n.reply.GetObserved()
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId()), Position: proto.Clone(v.Center).(*c.Cell)}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
}
func (n *equipTestNative) census() *o.ListPawnsReply {
	v := n.reply.GetObserved()
	var rows []*o.PawnState
	for _, id := range n.ids {
		rows = append(rows, n.pawn(id))
	}
	count := uint64(len(rows))
	return &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: rows, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(count), Returned: proto.Uint64(count), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}
}
func (n *equipTestNative) ReadRoutinePawns(ctx context.Context, _ *c.Identity, _ []string) (*o.ListPawnsReply, bridge.Result, error) {
	return n.census(), bridge.Result{}, ctx.Err()
}
func (n *equipTestNative) ReadCombatPawns(ctx context.Context, _ *c.Identity, _ []string) (*o.ListPawnsReply, bridge.Result, error) {
	return n.census(), bridge.Result{}, ctx.Err()
}
func (n *equipTestNative) ReadEmergency(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	v, r, err := n.routineNative.ReadEmergency(ctx, id)
	for _, id := range n.ids {
		v.Facts.Colonists = append(v.Facts.Colonists, policy.EmergencyPawn{ID: policy.PawnID(id), Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)})
	}
	return v, r, err
}
func (n *equipTestNative) ReadMapBounds(ctx context.Context, _ *c.Identity, _ domain.Cell) (bridge.MapBounds, bridge.Result, error) {
	return bridge.MapBounds{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Bounds: policy.Bounds{Width: 250, Height: 250}}, bridge.Result{}, ctx.Err()
}
func (n *equipTestNative) ReadEquipWeapons(ctx context.Context, _ *c.Identity, _, _ domain.Cell) (bridge.EquipRead, bridge.Result, error) {
	return bridge.EquipRead{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Targets: []bridge.EquipCandidate{{Thing: "bow", Definition: "Bow_Short", Cell: domain.Cell{X: 5, Z: 5}, Token: "bow-token"}}}, bridge.Result{}, ctx.Err()
}

// A pawn another planner already claimed this step must not starve the
// other unarmed colonists: colony-2 ended with every survivor unarmed
// beside loose bows because SelectEquip always named the same pawn.
func TestEquipPlannerSkipsClaimedPawn(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	reviewer, db, _, _, native := routineFixture(t)
	v := native.reply.GetObserved()
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	v.Issues = append(v.Issues, &o.ReadIssue{Field: proto.String("naming"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}})
	n := &equipTestNative{routineNative: native, ids: []string{"a", "b"}}
	reviewer.native = n
	reviewer.methods = domain.Known([]policy.GoalID{policy.EnsureBasicDefense})
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	review, err := db.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	found := false
	for _, binding := range review.Goals {
		if binding.Need != policy.EnsureBasicDefense {
			continue
		}
		g, err := db.LoadGoal(ctx, binding.Goal)
		if err != nil || g.Goal.Need != domain.NeedDeficit {
			t.Fatal("EnsureBasicDefense is not in deficit", g, err)
		}
		found = true
	}
	if !found {
		t.Fatal("EnsureBasicDefense missing from the review", review.Goals)
	}
	planner, err := NewRoutineEquipPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	call, epoch, done, err := reviewer.player.enter(ctx, false)
	if err != nil {
		t.Fatal(err)
	}
	defer done()
	arbiter := newStepArbiter()
	if !arbiter.tryClaim([]domain.PawnID{"a"}) {
		t.Fatal("fresh arbiter refused a claim")
	}
	result, err := planner.step(call, epoch, arbiter)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 1 {
		t.Fatal(plan, err)
	}
	equip, ok := plan.Spec.Actions()[0].Equip()
	if !ok || equip.Pawn() != "b" {
		t.Fatal("expected the unclaimed pawn b to equip", equip)
	}
}
