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

// routineWasteNative reuses routineNative's generic colony/identity reads,
// adds RoutineWasteSource's ReadTendPawns (delegating to the same pawn
// census ReadRoutinePawns already exposes) and overrides ReadEmergency so a
// single known, healthy hauler is available -- required for
// policy.RoutineWorkers to resolve the concurrent-development-project
// worker count the MaintainWaste arbitration gate depends on.
type routineWasteNative struct {
	*routineNative
}

func (n *routineWasteNative) ReadTendPawns(ctx context.Context, identity *c.Identity, ids []string) (*o.ListPawnsReply, bridge.Result, error) {
	return n.routineNative.ReadRoutinePawns(ctx, identity, ids)
}
func (n *routineWasteNative) ReadEmergency(ctx context.Context, identity *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	v, receipt, err := n.routineNative.ReadEmergency(ctx, identity)
	v.Facts.Colonists = []policy.EmergencyPawn{
		{ID: "hauler", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)},
		{ID: "hauler2", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)},
	}
	return v, receipt, err
}

func TestRoutineWastePlannerSelectsAndCommitsMethod(t *testing.T) {
	t.Parallel()
	reviewer, db, _, _, native := routineFixture(t)
	v := native.reply.GetObserved()
	// Two armed colonists, not one: routineFixture's playerAcquire already put
	// a player-submitted Wall plan in flight, consuming the sole development
	// slot a single-worker capacity would offer, and an unarmed lone colonist
	// would tie EnsureBasicDefense for top score and lose the alphabetic
	// tie-break. Two armed workers give capacity=2 (one free slot after the
	// existing commitment) with EnsureBasicDefense's deficit satisfied.
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	v.Waste = &o.WasteReply{Outcome: &o.WasteReply_Observed{Observed: &o.WasteSnapshot{
		Context: proto.Clone(v.Context).(*c.ObservationContext),
		Items: []*o.WasteItem{{
			Thing:    &o.EntityRef{Id: proto.String("junk-1"), DefName: proto.String("Filth_Trash"), MapId: proto.Int32(v.Context.Identity.GetMapId()), Position: &c.Cell{X: proto.Int32(5), Z: proto.Int32(6)}},
			Kind:     proto.String("junk"),
			State:    o.WasteLocation_WASTE_LOCATION_EXPOSED.Enum(),
			Eligible: proto.Bool(true),
		}},
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)},
	}}}
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	newRow := func(id string) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	}
	rows := []*o.PawnState{newRow("hauler"), newRow("hauler2")}
	native.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: rows, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(2), Returned: proto.Uint64(2), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}
	source := &routineWasteNative{native}
	reviewer.native = source
	ctx := context.Background()
	got, err := reviewer.Step(ctx)
	if err != nil {
		t.Fatal(err)
	}
	found := false
	for _, row := range got.Review.Development.Rows {
		if row.Goal == policy.MaintainWaste {
			found = row.Selected
		}
	}
	if !found {
		t.Fatal("MaintainWaste was not selected by development arbitration", got.Review.Development.Rows)
	}
	planner, err := NewRoutineWastePlanner(reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 1 {
		t.Fatal(plan, err)
	}
	action := plan.Spec.Actions()[0]
	waste, ok := action.Waste()
	if !ok || waste.Pawn() != "hauler" || waste.Target() != "junk-1" {
		t.Fatal(waste, ok)
	}
	next, err := planner.Step(ctx)
	if err != nil || next.Reason != BuildingMethodExistingWork {
		t.Fatal(next, err)
	}
}

func TestRoutineWastePlannerRefusesWithoutPendingCensus(t *testing.T) {
	t.Parallel()
	reviewer, _, _, _, native := routineFixture(t)
	if _, err := reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	source := &routineWasteNative{native}
	planner, err := NewRoutineWastePlanner(reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(context.Background())
	if err != nil || result.Reason == BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
}
