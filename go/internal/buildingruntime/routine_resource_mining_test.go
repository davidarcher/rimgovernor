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

// remoteOreNative offers steel only from a far steel lump and a nearer rock
// already under a foreign (non-owned) mining designation.
type remoteOreNative struct {
	*starvingResourceNative
}

func (n *remoteOreNative) ReadResourceSources(_ context.Context, _ *c.Identity, resource string) ([]bridge.ResourceSourceRow, policy.ResourceStorage, bridge.Result, error) {
	n.reads = append(n.reads, resource)
	storage := policy.ResourceStorage{Resource: policy.Resource(resource), Capacity: 1000, StackLimit: 75, Haulers: 2}
	if resource != "Steel" {
		return nil, storage, bridge.Result{}, nil
	}
	return []bridge.ResourceSourceRow{
		{ThingID: "foreign", Yield: 40, Distance: 3, Method: policy.ResourceSourceMine, Designated: true, Safety: "open_surface", Reachable: domain.Known(true), Cell: domain.Cell{X: 2, Z: 3}, Token: "foreign-cas"},
		{ThingID: "lump", Yield: 160, Distance: 90, Method: policy.ResourceSourceMine, Safety: "open_surface", Reachable: domain.Known(true), Cell: domain.Cell{X: 2, Z: 2}, Token: "lump-cas"},
	}, storage, bridge.Result{}, nil
}

// remoteOreStep runs one MaintainResource step for a two-worker colony with
// a 200-steel target and the given steel stock. It replaces the native
// mining/remote_ore case (#738).
func remoteOreStep(t *testing.T, steel int64) (RoutineResourceResult, *remoteOreNative) {
	t.Helper()
	base, _, _, _, sleeping := sleepingFixture(t)
	base.reviewer.policy.ResourceTargets = map[policy.Resource]int64{"Steel": 200}
	v := sleeping.reply.GetObserved()
	v.Resources = []*o.Quantity{{DefName: proto.String("Steel"), Units: proto.Int64(steel)}}
	native := &remoteOreNative{&starvingResourceNative{resourceNative: &resourceNative{
		workshopNative: &workshopNative{sleepingNative: sleeping},
	}}}
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
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
	// Both rocks lie inside the known colony extent.
	wall, err := domain.NewBuilding("Wall", domain.Cell{X: 2, Z: 2}, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	cells := []domain.Cell{{X: 2, Z: 2}, {X: 2, Z: 3}}
	facts := &base.reviewer.census.latest.reading.Projection.Facts
	facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{{ID: "wall", Building: wall, Cells: cells}}})
	facts.HomeCoverage = domain.Known(policy.HomeCoverageObservation{Targets: []policy.HomeCoverageTarget{{ID: "wall", Shape: domain.Known("shape"), Cells: cells, Missing: domain.Known(int64(0)), Excluded: domain.Known(int64(0)), ExtentGeometry: domain.Known(policy.HomeExtentGeometry{})}}})
	facts.MapBounds = domain.Known(policy.Bounds{Width: 100, Height: 100})
	planner, err := NewRoutineResourcePlanner(base.reviewer, native)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	return result, native
}

// A steel demand mines the far lump and never the rock under a foreign
// designation, whose yield it only reserves.
func TestSteelDemandMinesTheRemoteLumpNotTheForeignDesignation(t *testing.T) {
	t.Parallel()
	result, native := remoteOreStep(t, 0)
	if result.Reason != BuildingMethodAdmitted || len(native.acquisitions) != 1 || native.acquisitions[0].Token != "lump-cas" {
		t.Fatal(result, native.acquisitions)
	}
}

// With the steel delivered the demand is met: no further mining, and the
// foreign designation stays untouched.
func TestDeliveredSteelPlansNoFurtherMining(t *testing.T) {
	t.Parallel()
	result, native := remoteOreStep(t, 200)
	if result.Reason == BuildingMethodAdmitted || len(native.acquisitions) != 0 {
		t.Fatal(result, native.acquisitions)
	}
}
