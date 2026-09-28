package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// fullSteelStorageNative is the workshop fixture with no steel recipe, no
// steel source and no free steel storage: native offers unroofed candidate
// cells, as it does for a definition that does not deteriorate outdoors.
type fullSteelStorageNative struct {
	*resourceNative
	previews []domain.ZoneCreate
	// advance moves the preview read past the projection tick, as a clock
	// left running during planning does.
	advance int64
}

func (n *fullSteelStorageNative) ReadResourceSources(_ context.Context, _ *c.Identity, resource string) ([]bridge.ResourceSourceRow, policy.ResourceStorage, bridge.Result, error) {
	return nil, policy.ResourceStorage{Resource: policy.Resource(resource), StackLimit: 75, Haulers: 2, Candidates: []domain.Cell{{X: 3, Z: 3}, {X: 3, Z: 4}}}, bridge.Result{}, nil
}

func (n *fullSteelStorageNative) PreviewZone(_ context.Context, _ *c.Identity, target domain.ZoneCreate) (*op.ZonePreviewReply, bridge.Result, error) {
	n.previews = append(n.previews, target)
	read := proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext)
	read.Tick = proto.Int64(read.GetTick() + n.advance)
	return &op.ZonePreviewReply{Outcome: &op.ZonePreviewReply_Evaluated{Evaluated: &op.ZonePreview{Context: read, Accepted: proto.Bool(true)}}}, bridge.Result{}, nil
}

// A steel deficit with full storage admits one stack of stockpile before
// any acquisition bid is weighed: a rival trade bid used to outrank the
// planner's zero-score mining bid, so the storage branch never ran and
// every remote salvage target held missing_storage.
func TestResourceStorageFloorPrecedesRivalBids(t *testing.T) {
	t.Parallel()
	resourceStorageFloor(t, 0)
}

// A preview read one tick newer than the projection still admits: the clock
// keeps running during planning, so only a read older than the anchor is stale.
func TestResourceStorageFloorAdmitsNewerPreviewRead(t *testing.T) {
	t.Parallel()
	resourceStorageFloor(t, 1)
}

func resourceStorageFloor(t *testing.T, advance int64) {
	base, _, _, _, sleeping := sleepingFixture(t)
	base.reviewer.policy.ResourceTargets = map[policy.Resource]int64{"Steel": 200}
	sleeping.reply.GetObserved().Resources = []*o.Quantity{{DefName: proto.String("Steel"), Units: proto.Int64(0)}}
	native := &fullSteelStorageNative{resourceNative: &resourceNative{
		workshopNative: &workshopNative{sleepingNative: sleeping, benches: []bridge.GearBenchRead{{Token: "bench-cas", Bench: policy.GearBench{ID: "Thing_CraftingSpot1", Bills: domain.Known([]policy.GearBill{}), Recipes: domain.Known([]policy.GearRecipe{})}}}},
	}, advance: advance}
	v := sleeping.reply.GetObserved()
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	zonesAvailable(v)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	worker := func(id string) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	}
	sleeping.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{worker("crafter"), worker("builder")}, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}}}
	base.reviewer.native = native
	if _, err := base.reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineResourcePlanner(base.reviewer, native)
	if err != nil {
		t.Fatal(err)
	}
	p := base.reviewer.player
	ctx, epoch, done, err := p.enter(context.Background(), false)
	if err != nil {
		t.Fatal(err)
	}
	defer done()
	state := p.session.State()
	review, err := p.journal.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	var goal store.GoalState
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainResource {
			if goal, err = p.journal.LoadGoal(ctx, binding.Goal); err != nil {
				t.Fatal(err)
			}
		}
	}
	if goal.Goal.Status != domain.GoalActive {
		t.Fatal(goal)
	}
	base.reviewer.bids.bid(state.Snapshot, "Steel", bidTrade, 5, policy.AcquisitionTrade, review.Tick)
	result, err := planner.dispatchResourceGoal(ctx, epoch, state, goal, review.Tick, boundary.Identity(state.Snapshot), "Steel", 200, resourceStockFacts(v), nil, base.reviewer.clock.Now())
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	if len(native.previews) != 1 || len(native.previews[0].Cells()) != 1 {
		t.Fatal(native.previews)
	}
}
