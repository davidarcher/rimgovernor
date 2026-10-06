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

// roundsBlightNative reuses roundsNative's colony read, whose planning window
// carries the blighted plants as plant things; ReadEmergency lists two
// healthy colonists so development arbitration has a slot for RemoveBlight
// (see roundsWasteNative).
type roundsBlightNative struct {
	*roundsNative
}

func (n *roundsBlightNative) ReadEmergency(ctx context.Context, identity *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	v, receipt, err := n.roundsNative.ReadEmergency(ctx, identity)
	v.Facts.Colonists = []policy.EmergencyPawn{
		{ID: "cutter", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)},
		{ID: "cutter2", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)},
	}
	return v, receipt, err
}

func TestRoundsBlightPlannerDesignatesUndesignatedCensusPlants(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	reviewer, db, _, _, native := roundsFixture(t)
	v := native.reply.GetObserved()
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	zonesAvailable(v)
	// Ten blighted plants on home ground; the first already has a cut order.
	window := fixtureCells(t)
	window.Region = policy.Rectangle{Width: 30, Height: 10}
	window.Cells = nil
	for i := int32(0); i < 10; i++ {
		flags := policy.ThingFlags(0)
		if i == 0 {
			flags = policy.FlagDesignated
		}
		window.Cells = append(window.Cells, policy.SiteCell{Cell: domain.Cell{X: 10 + i, Z: 4}, InHome: domain.Known(true),
			Things: []policy.Thing{{Def: "Plant_Rice", Category: policy.ThingPlant, ID: uint64(1 + i), Count: 1, Flags: flags, Plant: policy.PlantState{Blighted: true}}}})
	}
	native.cells = window
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	newRow := func(id string) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	}
	native.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{newRow("cutter"), newRow("cutter2")}, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}}}
	source := &roundsBlightNative{roundsNative: native}
	reviewer.native = source
	ctx := context.Background()
	got, err := reviewer.Step(ctx)
	if err != nil {
		t.Fatal(err)
	}
	selected := false
	for _, row := range got.Review.Development.Rows {
		selected = selected || row.Concern == policy.RemoveBlight && row.Selected
	}
	if !selected {
		t.Fatal("RemoveBlight was not selected by development arbitration", got.Review.Development.Rows)
	}
	planner, err := NewRoundsBlightPlanner(reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 8 {
		t.Fatal(plan, err)
	}
	for i, p := range plan.Progress {
		cut, ok := p.Action().CutPlant()
		if !ok || p.View().Stage != domain.Pending || cut.Plant() == "Thing_Plant_Rice1" || cut.Cell().Z != 4 {
			t.Fatal("designated plant proposed or wrong shape", i, p)
		}
	}
	root := reviewer.player.session.State().Snapshot
	target := root
	target.Plan, target.Revision = plan.Spec.ID(), plan.Spec.Revision()
	if err = db.AuthorizeRoundsPlan(ctx, root, target); err != nil {
		t.Fatal(err)
	}
	if work, _, err := clockSchedulerWork(plan, target); err != nil || !work {
		t.Fatal("a cut designation must open a simulation window for the cutter", work, err)
	}
	if next, err := planner.Step(ctx); err != nil || next.Verdict != BuildingReasonExistingWork {
		t.Fatal(next, err)
	}
}

func TestRoundsBlightPlannerRefusesWithoutCensus(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	reviewer, _, _, _, native := roundsFixture(t)
	if _, err := reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoundsBlightPlanner(reviewer, &roundsBlightNative{roundsNative: native})
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(context.Background())
	if err != nil || result.Verdict == BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
}

func (n *roundsBlightNative) ReadRoundsFrame(ctx context.Context, id *c.Identity) (bridge.RoundsFrame, error) {
	return fakeFrame(ctx, n, id)
}
