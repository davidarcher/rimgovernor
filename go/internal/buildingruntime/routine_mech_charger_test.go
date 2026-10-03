package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// chargerNative reports the two colonists the pawn rows describe, so the
// review knows its workers and development arbitration can select the goal.
type chargerNative struct{ *sleepingNative }

func (n chargerNative) ReadEmergency(ctx context.Context, identity *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	v, receipt, err := n.sleepingNative.ReadEmergency(ctx, identity)
	for _, id := range []policy.PawnID{"crafter", "builder"} {
		v.Facts.Colonists = append(v.Facts.Colonists, policy.EmergencyPawn{ID: id, Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)})
	}
	return v, receipt, err
}

func (n chargerNative) ReadRoutineFrame(ctx context.Context, id *c.Identity) (bridge.RoutineFrame, error) {
	return fakeFrame(ctx, n, id)
}

type chargerDispatch struct {
	planner  *RoutineMechChargerPlanner
	call     context.Context
	epoch    context.Context
	db       *store.Store
	sleeping *sleepingNative
	review   store.RoutineReview
}

// mechChargerFixture is a colony with one mechanitor, the given chargers and
// a catalog whose only mech charger carries the native flag under a name no
// Go code knows.
func mechChargerFixture(t *testing.T, chargers []*o.MechChargerState, mechanitor bool) chargerDispatch {
	t.Helper()
	base, db, _, _, sleeping := sleepingFixture(t)
	base.reviewer.methods = domain.Known([]policy.GoalID{policy.EnsureMechCharger})
	v := sleeping.reply.GetObserved()
	v.Biotech = &o.BiotechSection{Outcome: &o.BiotechSection_Observed{Observed: &o.BiotechColonyFacts{Chargers: chargers}}}
	sleeping.catalog = []*o.PlanningDefinition{
		{Definition: &o.DefinitionRef{DefName: proto.String("Recharger_Test")}, MechCharger: proto.Bool(true), ConstructionSkill: proto.Int32(0), Size: &o.MapSize{Width: proto.Uint32(1), Height: proto.Uint32(2)}},
		{Definition: &o.DefinitionRef{DefName: proto.String("Wall")}, ConstructionSkill: proto.Int32(0), Size: &o.MapSize{Width: proto.Uint32(1), Height: proto.Uint32(1)}},
	}
	for i := range sleeping.cells.Cells {
		sleeping.cells.Cells[i].Doorway = domain.Known(false)
	}
	v.ColonistCount, v.WorkerCount = proto.Uint32(2), proto.Uint32(2)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	colonist := func(id string) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	}
	pawns := []*o.PawnState{colonist("crafter"), colonist("builder")}
	if mechanitor {
		pawns[0].Biotech = &o.PawnBiotech{Mechanitor: &o.PawnMechanitor{ControlGroups: proto.Int32(2)}}
	}
	for _, row := range pawns {
		sleeping.pawn(row)
	}
	sleeping.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: v.Context, Pawns: pawns, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}}}
	sleeping.onPreview = func(_ context.Context, p *bridge.BuildingPreview) {
		p.Preview.WatchCellsAccessible = domain.Known(true)
	}
	source := chargerNative{sleeping}
	base.reviewer.native = source
	review, err := base.reviewer.Step(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineMechChargerPlanner(base.reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	call, epoch, done, err := base.reviewer.player.enter(context.Background(), "test", false)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(done)
	return chargerDispatch{planner: planner, call: call, epoch: epoch, db: db, sleeping: sleeping, review: review.Review}
}

func busyCharger() *o.MechChargerState {
	return &o.MechChargerState{ThingId: proto.String("charger"), DefName: proto.String("Recharger_Test"), Position: &c.Cell{X: proto.Int32(4), Z: proto.Int32(4)},
		Powered: proto.Bool(true), ChargingMechId: proto.String("mech"), FullOfWaste: proto.Bool(false)}
}

// With every charger busy a charger is owed: the planner admits one through
// the ordinary building path on a footprint the native preview accepts, at
// the catalog-flagged definition, the farthest from the avoid sets.
func TestMechChargerPlannerAdmitsOneChargerWhenAllAreBusy(t *testing.T) {
	d := mechChargerFixture(t, []*o.MechChargerState{busyCharger()}, true)
	result, err := d.planner.step(d.call, d.epoch, nil)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err, d.review.Development.Rows)
	}
	plan, err := d.db.LoadPlan(context.Background(), result.Decision.Goal.Methods[len(result.Decision.Goal.Methods)-1].Plan)
	if err != nil {
		t.Fatal(err)
	}
	building, ok := plan.Spec.Actions()[0].Building()
	if !ok || building.Definition() != "Recharger_Test" || len(plan.Spec.Actions()) != 1 {
		t.Fatal(plan.Spec.Actions())
	}
	if plan.Progress[0].View().Stage != domain.Pending {
		t.Fatal("placement bypassed Hands")
	}
	if next, err := d.planner.step(d.call, d.epoch, nil); err != nil || next.Verdict != BuildingReasonExistingWork {
		t.Fatal("a charger being built is not staged again", next, err)
	}
}

// A refused preview moves on to the next ranked footprint; with none
// accepted nothing is admitted.
func TestMechChargerPlannerSkipsRefusedFootprintsAndReportsNoSpace(t *testing.T) {
	d := mechChargerFixture(t, []*o.MechChargerState{busyCharger()}, true)
	refuse := 0
	d.sleeping.onPreview = func(_ context.Context, p *bridge.BuildingPreview) {
		refuse++
		p.Preview.WatchCellsAccessible = domain.Known(true)
		p.Preview.CanPlace = domain.Known(false)
	}
	result, err := d.planner.step(d.call, d.epoch, nil)
	if err != nil || result.Verdict != BuildingReasonNoSpace || refuse < 2 {
		t.Fatal(result, err, refuse)
	}
}

// An idle charger, no charger owed, no mechanitor or an unread colony stage
// nothing and preview nothing.
func TestMechChargerPlannerStagesNothingUnlessOwed(t *testing.T) {
	idle := busyCharger()
	idle.ChargingMechId = nil
	for name, d := range map[string]chargerDispatch{
		"idle charger":  mechChargerFixture(t, []*o.MechChargerState{idle}, true),
		"no mechanitor": mechChargerFixture(t, nil, false),
	} {
		result, err := d.planner.step(d.call, d.epoch, nil)
		if err != nil || result.Verdict == BuildingReasonAdmitted || d.sleeping.previews != 0 {
			t.Errorf("%s: %v %v previews=%d", name, result, err, d.sleeping.previews)
		}
	}
}

func TestMechChargerDefinitionsAreFoundByTheCatalogFlag(t *testing.T) {
	d := mechChargerFixture(t, []*o.MechChargerState{busyCharger()}, true)
	if !footprintIsRect([]domain.Cell{{X: 1, Z: 1}, {X: 1, Z: 2}}, policy.Rectangle{X: 1, Z: 1, Width: 1, Height: 2}) ||
		footprintIsRect([]domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 2}}, policy.Rectangle{X: 1, Z: 1, Width: 1, Height: 2}) {
		t.Fatal("footprint must be exactly the rectangle")
	}
	if d.planner == nil {
		t.Fatal("planner")
	}
}
