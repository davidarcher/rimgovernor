package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// roundsFireNative is roundsWasteNative's shape for RoundsFireSafetySource:
// the generic colony read plus one known colonist for the tend pawn read.
type roundsFireNative struct {
	*roundsNative
}

func (n *roundsFireNative) ReadTendPawns(ctx context.Context, identity *c.Identity, ids []string) (*o.ListPawnsReply, bridge.Result, error) {
	return n.roundsNative.ReadRoundsPawns(ctx, identity, ids)
}
func (n *roundsFireNative) ReadEmergency(ctx context.Context, identity *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	v, receipt, err := n.roundsNative.ReadEmergency(ctx, identity)
	v.Facts.Colonists = []policy.EmergencyPawn{{ID: "firefighter", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)}}
	return v, receipt, err
}

func fireSafetyFixture(t *testing.T, size float64, firefighting bool) (*RoundsFireSafetyPlanner, *Rounder) {
	t.Helper()
	reviewer, _, _, _, native := roundsFixture(t)
	v := native.reply.GetObserved()
	v.ColonistCount, v.WorkerCount = proto.Uint32(1), proto.Uint32(1)
	v.Upkeep = &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: &o.UpkeepFacts{
		Fires:   []*o.FireState{{Fire: native.entity(&o.EntityRef{Id: proto.String("fire-1"), DefName: proto.String("Fire"), MapId: proto.Int32(0), Position: &c.Cell{X: proto.Int32(3), Z: proto.Int32(3)}}), Size: proto.Float64(size), Home: proto.Bool(true)}},
		Comfort: &o.ComfortSection{Outcome: &o.ComfortSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_REQUESTED.Enum()}}},
	}}}
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("firefighter"), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false),
		Job:       &o.JobEvidence{DefName: proto.String("Wait"), PlayerForced: proto.Bool(false)},
		Health:    &o.PawnHealth{NeedsTend: proto.Bool(false), Bleeding: proto.Bool(false)},
		Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Biography: &o.PawnBiography{},
		Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true), Work: []*o.WorkSetting{{DefName: proto.String("Firefighter"), Priority: proto.Int32(1), Disabled: proto.Bool(!firefighting)}}},
		Issues:   []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	native.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{row}, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}}}
	source := &roundsFireNative{native}
	reviewer.native = source
	if _, err := reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoundsFireSafetyPlanner(reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	return planner, reviewer
}

// A bounded home fire with an eligible firefighter is fought natively: the
// planner commits nothing and instead asks for a short clock window, which
// is the only way NeedsTend-style native work can clear the deficit.
func TestRoundsFireSafetyWaitsForNativeFirefightingWithClockTicks(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	planner, _ := fireSafetyFixture(t, 0.9, true)
	result, err := planner.Step(context.Background())
	if err != nil || result.Outcome != policy.FireSafetyWaitingForNative || result.NativeWorkTicks != fireSafetyNativeWorkTicks {
		t.Fatal(result, err)
	}
}

// Without an eligible firefighter the emergency hold stays: no window.
func TestRoundsFireSafetyHoldsWithoutFirefighter(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	planner, _ := fireSafetyFixture(t, 0.9, false)
	result, err := planner.Step(context.Background())
	if err != nil || result.Outcome != policy.FireSafetyBlocked || result.NativeWorkTicks != 0 {
		t.Fatal(result, err)
	}
}

// A fire ReviewUpkeep calls unsafe (size above one) still gets bounded native
// windows: holding the clock would freeze the emergency forever.
func TestRoundsFireSafetyTicksBoundedOnUnsafeFire(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	planner, _ := fireSafetyFixture(t, 1.5, true)
	result, err := planner.Step(context.Background())
	if err != nil || result.Outcome != policy.FireSafetyWaitingForNative || result.NativeWorkTicks != fireSafetyNativeWorkTicks {
		t.Fatal(result, err)
	}
}

func (n *roundsFireNative) ReadRoundsFrame(ctx context.Context, id *c.Identity) (bridge.RoundsFrame, error) {
	return fakeFrame(ctx, n, id)
}
