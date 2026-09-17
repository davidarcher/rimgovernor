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

// sleepingUpkeepNative lists both colonists in the emergency census so the
// work-priority read covers every worker and the labor ledger is known.
type sleepingUpkeepNative struct{ *hospitalNative }

func (n *sleepingUpkeepNative) ReadEmergency(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	v, r, e := n.sleepingNative.ReadEmergency(ctx, id)
	for _, id := range []policy.PawnID{"patient", "other"} {
		v.Facts.Colonists = append(v.Facts.Colonists, policy.EmergencyPawn{ID: id, Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)})
	}
	return v, r, e
}

// sleepingUpkeepFixture stages one barracks at 20C holding one unowned,
// suitable Bed and one colonist (comfortable 10..30C) who owns nothing:
// MaintainSleeping is in deficit and the bed can be assigned outright.
func sleepingUpkeepFixture(t *testing.T) (*RoutineSleepingUpkeepPlanner, *store.Store, *sleepingUpkeepNative) {
	t.Helper()
	base, db, _, _, sleeping := sleepingFixture(t)
	native := &sleepingUpkeepNative{hospitalNative: &hospitalNative{sleepingNative: sleeping}}
	v := native.reply.GetObserved()
	// Two colonists: the fixture's submitted player project holds one
	// development slot and the sleeping goal (priority 3) needs the other.
	// The second colonist already sleeps in an owned bed, so only the
	// patient is a sleeping target.
	v.ColonistCount, v.WorkerCount = proto.Uint32(2), proto.Uint32(2)
	planning := v.Planning.GetObserved()
	planning.Definitions = append(planning.Definitions, &o.PlanningDefinition{Definition: &o.DefinitionRef{DefName: proto.String("Bed")}, Available: proto.Bool(true), ConstructionSkill: proto.Int32(0), Size: &o.MapSize{Width: proto.Uint32(1), Height: proto.Uint32(2)}})
	planning.Completeness.Matched, planning.Completeness.Returned = proto.Uint64(uint64(len(planning.Definitions))), proto.Uint64(uint64(len(planning.Definitions)))
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	v.Issues = append(v.Issues, missing("naming"))
	pawn := func(id string) *o.PawnState {
		row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)},
			Health: &o.PawnHealth{NeedsTend: proto.Bool(false), Bleeding: proto.Bool(false), ShouldSeekMedicalRest: proto.Bool(false), HediffCompleteness: hospitalCount(0), HiddenHediffs: proto.Uint32(0)},
			Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
		for _, skill := range []string{"Construction", "Plants", "Cooking", "Medicine", "Shooting"} {
			row.Biography.Skills = append(row.Biography.Skills, &o.Skill{Definition: &o.DefinitionRef{DefName: proto.String(skill)}, Level: proto.Int32(10), Disabled: proto.Bool(false), Passion: proto.String("None")})
		}
		for _, work := range []string{"Construction", "Growing", "Cooking", "Doctor", "PlantCutting", "Firefighter"} {
			row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String(work), Priority: proto.Int32(1), Disabled: proto.Bool(false)})
		}
		return row
	}
	native.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{pawn("patient"), pawn("other")}, Completeness: hospitalCount(2)}}}
	cell := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	bed := &o.EntityRef{Id: proto.String("bed"), DefName: proto.String("Bed"), MapId: proto.Int32(0), Position: cell(0, 0)}
	room := &o.RoomState{Id: proto.String("42"), Role: proto.String("Barracks"), ProperRoom: proto.Bool(true), Doorway: proto.Bool(false), Outdoors: proto.Bool(false), PsychologicallyOutdoors: proto.Bool(false), TouchesMapEdge: proto.Bool(false), OpenRoofCount: proto.Uint32(0), CellCount: proto.Uint32(4), TemperatureC: proto.Float64(20), Center: cell(0, 0), Extents: &o.Rectangle{Minimum: cell(0, 0), Maximum: cell(1, 1)}, Cells: []*c.Cell{cell(0, 0), cell(0, 1), cell(1, 0), cell(1, 1)}, CellsCompleteness: hospitalCount(4), Contents: []*o.Quantity{{DefName: proto.String("Bed"), Units: proto.Int64(1)}}, ContentsCompleteness: hospitalCount(1), Beds: []*o.BuildingState{{Building: bed, Status: proto.String("built")}}}
	native.rooms = &o.ListRoomsReply{Outcome: &o.ListRoomsReply_Observed{Observed: &o.RoomsSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Completeness: hospitalCount(1), Rooms: []*o.RoomState{room}}}}
	person := func(id string, owned string) *o.UpkeepPerson {
		return &o.UpkeepPerson{Pawn: &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), DefName: proto.String("Human"), MapId: proto.Int32(0), Position: cell(1, 1)}}, OwnedBedId: proto.String(owned), ComfortableMinC: proto.Float64(10), ComfortableMaxC: proto.Float64(30)}
	}
	upkeepBed := func(ref *o.EntityRef, owners ...string) *o.UpkeepBed {
		return &o.UpkeepBed{Bed: ref, Slots: proto.Uint32(1), Humanlike: proto.Bool(true), Medical: proto.Bool(false), Prisoners: proto.Bool(false), Roofed: proto.Bool(true), RestEffectiveness: proto.Float64(1), TemperatureC: proto.Float64(20), AccessibleTo: []string{"patient", "other"}, Owners: owners, Users: owners}
	}
	otherBed := &o.EntityRef{Id: proto.String("bed-other"), DefName: proto.String("Bed"), MapId: proto.Int32(0), Position: cell(1, 0)}
	v.Upkeep = &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: &o.UpkeepFacts{Completeness: hospitalCount(1),
		People:  []*o.UpkeepPerson{person("patient", ""), person("other", "bed-other")},
		Beds:    []*o.UpkeepBed{upkeepBed(bed), upkeepBed(otherBed, "other")},
		Comfort: &o.ComfortSection{Outcome: &o.ComfortSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_REQUESTED.Enum()}}}}}}
	base.reviewer.native = native
	base.reviewer.methods = domain.Known([]policy.GoalID{policy.MaintainSleeping})
	if _, err := base.reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineSleepingUpkeepPlanner(base.reviewer, native)
	if err != nil {
		t.Fatal(err)
	}
	return planner, db, native
}

func sleepingGoal(t *testing.T, db *store.Store) store.GoalState {
	t.Helper()
	ctx := context.Background()
	review, err := db.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainSleeping {
			goal, err := db.LoadGoal(ctx, binding.Goal)
			if err != nil {
				t.Fatal(err)
			}
			return goal
		}
	}
	t.Fatal("sleeping goal not bound", review.Goals)
	return store.GoalState{}
}

func TestSleepingUpkeepAssignsVacantBedOncePerEpoch(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	planner, db, native := sleepingUpkeepFixture(t)
	goal := sleepingGoal(t, db)
	if goal.Goal.Need != domain.NeedDeficit {
		t.Fatal("sleeping goal not in deficit", goal.Goal)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	if native.previews != 0 {
		t.Fatal("assignment previewed a building", native.previews)
	}
	goal = sleepingGoal(t, db)
	if len(goal.Methods) != 1 || goal.Methods[0].Method != "sleeping-assign-patient-bed" {
		t.Fatal(goal.Methods)
	}
	plan, err := db.LoadPlan(ctx, goal.Methods[0].Plan)
	if err != nil || len(plan.Progress) != 1 {
		t.Fatal(plan, err)
	}
	assign, ok := plan.Progress[0].Action().BedAssign()
	if !ok || assign.Pawn() != "patient" || assign.Bed() != "bed" || !assign.PreviousBed().Clear() {
		t.Fatal(plan.Progress[0].Action())
	}
	// Open assignment is existing work; once retired, the used method is not
	// retried within the epoch even though the bed still reads vacant.
	if result, err = planner.Step(ctx); err != nil || result.Reason != BuildingMethodExistingWork {
		t.Fatal(result, err)
	}
	if _, err = db.Cancel(ctx, plan.Spec.ID(), plan.Progress[0].Action().ID()); err != nil {
		t.Fatal(err)
	}
	if result, err = planner.Step(ctx); err != nil || result.Reason != BuildingMethodUsed {
		t.Fatal(result, err)
	}
}

func TestSleepingUpkeepAssignmentNeverCompletesTheGoal(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	planner, db, native := sleepingUpkeepFixture(t)
	if result, err := planner.Step(ctx); err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	// The census now shows the bed owned by the sleeper: still in deficit,
	// nothing to dispatch, until the sleeper is observed using it.
	upkeep := native.reply.GetObserved().Upkeep.GetObserved()
	upkeep.Beds[0].Owners = []string{"patient"}
	upkeep.People[0].OwnedBedId = proto.String("bed")
	if _, err := planner.reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	goal := sleepingGoal(t, db)
	if goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		t.Fatal("assignment receipt completed the sleeping goal", goal.Goal)
	}
	if result, err := planner.Step(ctx); err != nil || (result.Reason != BuildingSleepingUseNeeded && result.Reason != BuildingMethodExistingWork) {
		t.Fatal(result, err)
	}
	upkeep.Beds[0].Users = []string{"patient"}
	if _, err := planner.reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	if goal = sleepingGoal(t, db); goal.Goal.Need == domain.NeedDeficit {
		t.Fatal("observed sleep did not recover the goal", goal.Goal)
	}
}

func TestSleepingUpkeepBuildsBedInWarmHostingRoom(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	planner, _, native := sleepingUpkeepFixture(t)
	// A bed another colonist owns cannot be reassigned, so a Bed is staged
	// in the barracks; the sleeping spot definition is never a fallback.
	native.reply.GetObserved().Upkeep.GetObserved().Beds[0].Owners = []string{"other"}
	native.onPreview = func(_ context.Context, p *bridge.BuildingPreview) {
		b, _ := p.Preview.Action.Building()
		p.Preview.Footprint = domain.Known([]domain.Cell{b.Cell()})
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted || native.previews == 0 {
		t.Fatal(result, err, native.previews)
	}
	if len(result.Decision.Goal.Methods) != 1 || result.Decision.Goal.Methods[0].Method != "sleeping-Bed-1" {
		t.Fatal(result.Decision.Goal.Methods)
	}
}

func TestSleepingUpkeepDoesNotBuildOutsideComfortBand(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	planner, _, native := sleepingUpkeepFixture(t)
	// A room outside the sleeper's comfortable band is not a site: no bed is
	// previewed there and the ladder falls through to its shell rung, which
	// waits while the initial shelter is still owed.
	native.reply.GetObserved().Upkeep.GetObserved().Beds[0].Owners = []string{"other"}
	native.rooms.GetObserved().Rooms[0].TemperatureC = proto.Float64(-5)
	result, err := planner.Step(ctx)
	if err != nil || result.Reason != BuildingShellBlocked || native.previews != 0 {
		t.Fatal(result, err, native.previews)
	}
}
