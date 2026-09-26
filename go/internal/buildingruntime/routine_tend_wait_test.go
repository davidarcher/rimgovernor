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

// routineTendNative serves RoutineTendSource over the colony-core fixture with
// one bleeding colonist whose bed state is unreadable -- the shape that holds
// CriticalMedical while no tend order can be built (#636).
type routineTendNative struct {
	*routineNative
}

func (n *routineTendNative) ReadTendPawns(ctx context.Context, identity *c.Identity, ids []string) (*o.ListPawnsReply, bridge.Result, error) {
	return n.routineNative.ReadRoutinePawns(ctx, identity, ids)
}
func (n *routineTendNative) ReadEmergency(ctx context.Context, identity *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	v, receipt, err := n.routineNative.ReadEmergency(ctx, identity)
	v.Facts.Colonists = []policy.EmergencyPawn{{ID: "patient", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(true), NeedsTend: domain.Known(true)}}
	return v, receipt, err
}

// A standing CriticalMedical deficit that yields no doctor/patient pair lends a
// bounded clock window instead of leaving the step with no work: the emergency
// freezes development and clears only on game time, so a step that reported no
// work parked the clock at a fixed tick for the rest of the run (#636).
func TestRoutineTendLendsClockTicksWithoutAnEligiblePair(t *testing.T) {
	t.Parallel()
	reviewer, _, _, _, native := routineFixture(t)
	v := native.reply.GetObserved()
	v.ColonistCount, v.WorkerCount = proto.Uint32(1), proto.Uint32(1)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	// Up, bleeding, needing tend, with Doctor disabled: nothing the tend
	// policy will select, and nothing another method can change.
	row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("patient"), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false),
		Job:       &o.JobEvidence{DefName: proto.String("Wait"), PlayerForced: proto.Bool(false)},
		Health:    &o.PawnHealth{NeedsTend: proto.Bool(true), Bleeding: proto.Bool(true)},
		Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}, Biography: &o.PawnBiography{},
		Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true), Work: []*o.WorkSetting{{DefName: proto.String("Doctor"), Priority: proto.Int32(0), Disabled: proto.Bool(true)}}},
		Issues:   []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	native.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{row}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}
	source := &routineTendNative{native}
	reviewer.native = source
	if _, err := reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineTendPlanner(reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if result.Reason != BuildingMethodUsed || result.Plan != "" {
		t.Fatal(result)
	}
	if result.NativeWorkTicks != medicalWaitTicks {
		t.Fatal(result.NativeWorkTicks)
	}
}
