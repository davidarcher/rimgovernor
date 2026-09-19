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

// routineBlightNative reuses routineNative's colony read and serves the
// blight planner's census from the same snapshot's blighted_plants rows;
// ReadEmergency lists two healthy colonists so development arbitration has
// a slot for RemoveBlight (see routineWasteNative).
type routineBlightNative struct {
	*routineNative
	reads int
}

func (n *routineBlightNative) ReadBlightedPlants(_ context.Context, _ *c.Identity) (bridge.CutPlantRead, bridge.Result, error) {
	n.reads++
	v := n.reply.GetObserved()
	out := bridge.CutPlantRead{Context: proto.Clone(v.Context).(*c.ObservationContext)}
	for _, row := range v.BlightedPlants {
		if row.GetDesignated() {
			continue
		}
		plant, _ := domain.NewCutPlant(row.Plant.GetId(), row.Plant.GetDefName(), domain.Cell{X: row.Plant.Position.GetX(), Z: row.Plant.Position.GetZ()})
		out.Targets = append(out.Targets, bridge.CutPlantTarget{Plant: plant, Token: row.Plant.Snapshot.GetToken()})
	}
	return out, bridge.Result{}, nil
}
func (n *routineBlightNative) ReadEmergency(ctx context.Context, identity *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	v, receipt, err := n.routineNative.ReadEmergency(ctx, identity)
	v.Facts.Colonists = []policy.EmergencyPawn{
		{ID: "cutter", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)},
		{ID: "cutter2", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)},
	}
	return v, receipt, err
}

func blightedRow(v *o.ColonyFactsSnapshot, id string, x, z int32, designated bool) *o.BlightedPlant {
	return &o.BlightedPlant{Plant: &o.EntityRef{Id: proto.String(id), DefName: proto.String("Plant_Rice"), MapId: proto.Int32(v.Context.Identity.GetMapId()),
		Position: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}, Snapshot: &o.SnapshotRef{EntityId: proto.String(id), Token: proto.String("cut-" + id), Context: proto.Clone(v.Context).(*c.ObservationContext)}},
		Designated: proto.Bool(designated), ZoneId: proto.String("7")}
}

func TestRoutineBlightPlannerDesignatesUndesignatedCensusPlants(t *testing.T) {
	t.Parallel()
	reviewer, db, _, _, native := routineFixture(t)
	v := native.reply.GetObserved()
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	for i := int32(0); i < 10; i++ {
		v.BlightedPlants = append(v.BlightedPlants, blightedRow(v, "Plant_Rice"+string(rune('a'+i)), 10+i, 4, i == 0))
	}
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	newRow := func(id string) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	}
	native.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{newRow("cutter"), newRow("cutter2")}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(2), Returned: proto.Uint64(2), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}
	source := &routineBlightNative{routineNative: native}
	reviewer.native = source
	ctx := context.Background()
	got, err := reviewer.Step(ctx)
	if err != nil {
		t.Fatal(err)
	}
	selected := false
	for _, row := range got.Review.Development.Rows {
		selected = selected || row.Goal == policy.RemoveBlight && row.Selected
	}
	if !selected {
		t.Fatal("RemoveBlight was not selected by development arbitration", got.Review.Development.Rows)
	}
	planner, err := NewRoutineBlightPlanner(reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 8 {
		t.Fatal(plan, err)
	}
	for i, p := range plan.Progress {
		cut, ok := p.Action().CutPlant()
		if !ok || p.View().Stage != domain.Pending || cut.Plant() == "Plant_Ricea" || cut.Cell().Z != 4 {
			t.Fatal("designated plant proposed or wrong shape", i, p)
		}
	}
	root := reviewer.player.session.State().Snapshot
	target := root
	target.Plan, target.Revision = plan.Spec.ID(), plan.Spec.Revision()
	if err = db.AuthorizeRoutinePlan(ctx, root, target); err != nil {
		t.Fatal(err)
	}
	if work, _, err := clockSchedulerWork(plan, target); err != nil || !work {
		t.Fatal("a cut designation must open a simulation window for the cutter", work, err)
	}
	if next, err := planner.Step(ctx); err != nil || next.Reason != BuildingMethodExistingWork || source.reads != 1 {
		t.Fatal(next, err)
	}
}

func TestRoutineBlightPlannerRefusesWithoutCensus(t *testing.T) {
	t.Parallel()
	reviewer, _, _, _, native := routineFixture(t)
	if _, err := reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineBlightPlanner(reviewer, &routineBlightNative{routineNative: native})
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(context.Background())
	if err != nil || result.Reason == BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
}
