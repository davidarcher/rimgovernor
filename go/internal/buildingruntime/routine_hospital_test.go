package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// hospitalNative is a one-colonist colony whose colonist should rest in a
// medical bed but needs no tending: MaintainMedicalCare is in deficit while
// the tend and rescue families have nothing to do.
type hospitalNative struct {
	*sleepingNative
	rooms       *o.ListRoomsReply
	target      bridge.BedMedicalTarget
	targetReads int
	bedPreviews int
	refuse      bool
}

func (n *hospitalNative) ReadEmergency(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	v, r, e := n.sleepingNative.ReadEmergency(ctx, id)
	v.Facts.Colonists = []policy.EmergencyPawn{{ID: "patient", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)}}
	return v, r, e
}

func (n *hospitalNative) ReadTemperatureRooms(context.Context, *c.Identity) (*o.ListRoomsReply, bridge.Result, error) {
	return n.rooms, bridge.Result{}, nil
}

func (n *hospitalNative) ReadBedMedicalTarget(_ context.Context, _ *c.Identity, thing string) (bridge.BedMedicalTarget, bridge.Result, error) {
	n.targetReads++
	if thing != n.target.Thing {
		return bridge.BedMedicalTarget{}, bridge.Result{}, bridge.ErrContract
	}
	return n.target, bridge.Result{}, nil
}

func (n *hospitalNative) PreviewBedMedical(_ context.Context, _ *c.Identity, patch domain.BedMedical) (*op.PreviewReply, bridge.Result, error) {
	n.bedPreviews++
	if patch.Thing() != n.target.Thing || patch.BeforeToken() != n.target.Token || !patch.Medical() {
		return nil, bridge.Result{}, bridge.ErrContract
	}
	return &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Accepted: proto.Bool(!n.refuse)}}}, bridge.Result{}, nil
}

func hospitalCount(n uint64) *o.Completeness {
	return &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(n), Returned: proto.Uint64(n), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
}

// hospitalFixture stages one barracks holding one unowned, non-medical
// sleeping spot and one colonist who should seek medical rest.
func hospitalFixture(t *testing.T) (*RoutineHospitalPlanner, *store.Store, *hospitalNative) {
	t.Helper()
	base, db, _, _, sleeping := sleepingFixture(t)
	native := &hospitalNative{sleepingNative: sleeping}
	v := native.reply.GetObserved()
	v.ColonistCount, v.WorkerCount = proto.Uint32(1), proto.Uint32(1)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	v.Issues = append(v.Issues, missing("naming"))
	row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("patient"), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)},
		Health: &o.PawnHealth{NeedsTend: proto.Bool(false), Bleeding: proto.Bool(false), ShouldSeekMedicalRest: proto.Bool(true), HediffCompleteness: hospitalCount(0), HiddenHediffs: proto.Uint32(0)},
		Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	for _, skill := range []string{"Construction", "Plants", "Cooking", "Medicine", "Shooting"} {
		row.Biography.Skills = append(row.Biography.Skills, &o.Skill{Definition: &o.DefinitionRef{DefName: proto.String(skill)}, Level: proto.Int32(10), Disabled: proto.Bool(false), Passion: proto.String("None")})
	}
	for _, work := range []string{"Construction", "Growing", "Cooking", "Doctor", "PlantCutting", "Firefighter"} {
		row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String(work), Priority: proto.Int32(1), Disabled: proto.Bool(false)})
	}
	native.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{row}, Completeness: hospitalCount(1)}}}
	cell := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	bed := &o.EntityRef{Id: proto.String("bed"), DefName: proto.String("SleepingSpot"), MapId: proto.Int32(0), Position: cell(0, 0)}
	room := &o.RoomState{Id: proto.String("42"), Role: proto.String("Barracks"), ProperRoom: proto.Bool(true), Doorway: proto.Bool(false), Outdoors: proto.Bool(false), PsychologicallyOutdoors: proto.Bool(false), TouchesMapEdge: proto.Bool(false), OpenRoofCount: proto.Uint32(0), CellCount: proto.Uint32(4), TemperatureC: proto.Float64(20), Center: cell(0, 0), Extents: &o.Rectangle{Minimum: cell(0, 0), Maximum: cell(1, 1)}, Cells: []*c.Cell{cell(0, 0), cell(0, 1), cell(1, 0), cell(1, 1)}, CellsCompleteness: hospitalCount(4), Contents: []*o.Quantity{{DefName: proto.String("SleepingSpot"), Units: proto.Int64(1)}}, ContentsCompleteness: hospitalCount(1), Beds: []*o.BuildingState{{Building: bed, Status: proto.String("built")}}}
	native.rooms = &o.ListRoomsReply{Outcome: &o.ListRoomsReply_Observed{Observed: &o.RoomsSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Completeness: hospitalCount(1), Rooms: []*o.RoomState{room}}}}
	v.Upkeep = &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: &o.UpkeepFacts{Completeness: hospitalCount(1), Beds: []*o.UpkeepBed{{Bed: bed, Slots: proto.Uint32(1), Humanlike: proto.Bool(true), Medical: proto.Bool(false), Prisoners: proto.Bool(false), Roofed: proto.Bool(true), TemperatureC: proto.Float64(20)}},
		Comfort: &o.ComfortSection{Outcome: &o.ComfortSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_REQUESTED.Enum()}}}}}}
	native.target = bridge.BedMedicalTarget{Context: proto.Clone(v.Context).(*c.ObservationContext), Thing: "bed", Token: "bed-cas"}
	base.reviewer.native = native
	base.reviewer.methods = domain.Known([]policy.GoalID{policy.MaintainMedicalCare})
	if _, err := base.reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineHospitalPlanner(base.reviewer, native)
	if err != nil {
		t.Fatal(err)
	}
	return planner, db, native
}

func TestHospitalConvertsSpareHostedBedOncePerEpoch(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	planner, db, native := hospitalFixture(t)
	result, err := planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	if native.targetReads != 1 || native.bedPreviews != 1 || native.previews != 0 {
		t.Fatal("convert previewed a building", native.targetReads, native.bedPreviews, native.previews)
	}
	review, err := db.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	var goal store.GoalState
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainMedicalCare {
			if goal, err = db.LoadGoal(ctx, binding.Goal); err != nil {
				t.Fatal(err)
			}
		}
	}
	if len(goal.Methods) != 1 || goal.Methods[0].Method != "hospital-convert-bed" {
		t.Fatal(goal.Methods)
	}
	plan, err := db.LoadPlan(ctx, goal.Methods[0].Plan)
	if err != nil || len(plan.Progress) != 1 {
		t.Fatal(plan, err)
	}
	patch, ok := plan.Progress[0].Action().BedMedical()
	if !ok || patch.Thing() != "bed" || !patch.Medical() || patch.BeforeToken() != "bed-cas" {
		t.Fatal(plan.Progress[0].Action())
	}
	// The open patch is existing work; once it retires, the used method is
	// not retried within the epoch even though the bed still reads non-medical.
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

func TestHospitalAcceptsExistingMedicalBedAndRefusedPreview(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	planner, _, native := hospitalFixture(t)
	native.refuse = true
	if result, err := planner.Step(ctx); err != nil || result.Reason != BuildingMethodRefused {
		t.Fatal(result, err)
	}
	// The bed flips medical by the player's hand between reviews: the CAS
	// read, not the census, is what the convert path trusts.
	native.refuse = false
	native.target.Medical = true
	if result, err := planner.Step(ctx); err != nil || result.Reason != BuildingExistingFacility {
		t.Fatal(result, err)
	}
	// A hosted medical bed in the census settles the deficit without a read.
	native.reply.GetObserved().Upkeep.GetObserved().Beds[0].Medical = proto.Bool(true)
	native.targetReads = 0
	if result, err := planner.Step(ctx); err != nil || result.Reason != BuildingExistingFacility || native.targetReads != 0 {
		t.Fatal(result, err, native.targetReads)
	}
}

func TestHospitalBuildsOnlyWhenNoHostedBedCanBeSpared(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	planner, _, native := hospitalFixture(t)
	// A prisoner bed never hosts a colonist patient, so the ladder stages a
	// sleeping spot in the barracks (a Hospital-hosting room).
	native.reply.GetObserved().Upkeep.GetObserved().Beds[0].Prisoners = proto.Bool(true)
	native.onPreview = func(_ context.Context, p *bridge.BuildingPreview) {
		b, _ := p.Preview.Action.Building()
		p.Preview.Footprint = domain.Known([]domain.Cell{b.Cell()})
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted || native.bedPreviews != 0 || native.previews == 0 {
		t.Fatal(result, err, native.bedPreviews, native.previews)
	}
	if len(result.Decision.Goal.Methods) != 1 || result.Decision.Goal.Methods[0].Method != "hospital-SleepingSpot" {
		t.Fatal(result.Decision.Goal.Methods)
	}
}

func TestHospitalSelectMapsChoicesOntoTheLadder(t *testing.T) {
	t.Parallel()
	ladder := &RoutineBuildingPlanner{goal: policy.MaintainMedicalCare, definition: "Wall", shelter: true}
	patient := policy.CarePawn{ID: "p", Dead: domain.Known(false), NeedsRest: domain.Known(true), NeedsTend: domain.Known(false), BadConditions: domain.Known(false)}
	definition := func(name string, available bool) observation.PlanningDefinition {
		return observation.PlanningDefinition{Name: name, Available: domain.Known(available), NeedsPower: domain.Known(false), ConstructionSkill: domain.Known(int32(0)), Stuff: domain.Known("WoodLog")}
	}
	rooms := domain.Known(policy.RoomObservation{Rooms: []policy.Room{{ID: "b", Role: domain.Known(policy.RoomRoleBarracks), Beds: []string{"bed"}}}})
	bed := func(medical bool) domain.Fact[policy.SleepingObservation] {
		return domain.Known(policy.SleepingObservation{Beds: []policy.SleepingBed{{ID: "bed", Humanlike: domain.Known(true), Medical: domain.Known(medical), Prisoners: domain.Known(false)}}})
	}
	ill := domain.Known([]policy.CarePawn{patient})
	for _, test := range []struct {
		name   string
		facts  observation.ColonyProjection
		reason RoutineBuildingReason
		want   string
	}{
		{"unknown", observation.ColonyProjection{}, BuildingMethodUnknown, ""},
		{"no demand", observation.ColonyProjection{Facts: policy.RoutineFacts{MedicalPawns: domain.Known([]policy.CarePawn{})}}, BuildingMethodNoDeficit, ""},
		{"existing", observation.ColonyProjection{Facts: policy.RoutineFacts{MedicalPawns: ill, Sleeping: bed(true)}, Rooms: rooms}, BuildingExistingFacility, ""},
		{"convert", observation.ColonyProjection{Facts: policy.RoutineFacts{MedicalPawns: ill, Sleeping: bed(false)}, Rooms: rooms}, BuildingHospitalConvert, ""},
		{"build", observation.ColonyProjection{Facts: policy.RoutineFacts{MedicalPawns: ill, Sleeping: domain.Known(policy.SleepingObservation{})}, Rooms: rooms, Definitions: []observation.PlanningDefinition{definition("Bed", false), definition("SleepingSpot", true)}}, "", "SleepingSpot"},
		{"unavailable", observation.ColonyProjection{Facts: policy.RoutineFacts{MedicalPawns: ill, Sleeping: domain.Known(policy.SleepingObservation{})}, Rooms: rooms, Definitions: []observation.PlanningDefinition{definition("Bed", false), definition("SleepingSpot", false)}}, BuildingHospitalUnavailable, ""},
	} {
		selected, reason, err := ladder.selectHospital(test.facts)
		if err != nil || reason != test.reason {
			t.Fatal(test.name, selected, reason, err)
		}
		if test.want == "" {
			if selected != nil {
				t.Fatal(test.name, selected)
			}
			continue
		}
		if selected.definition != test.want || selected.stuff != "WoodLog" || selected.environment != policy.PlacementIndoors || selected.facility == nil || selected.facility.Role != policy.RoomRoleHospital {
			t.Fatal(test.name, selected)
		}
		if ladder.definition != "Wall" || ladder.facility != nil {
			t.Fatal("selection mutated reusable ladder", ladder)
		}
	}
	facts := observation.ColonyProjection{Facts: policy.RoutineFacts{Colonists: domain.Known(int64(2))}}
	if missing, method, reason := ladder.selection(facts); missing != 32 || method != "hospital-shell" || reason != "" {
		t.Fatal(missing, method, reason)
	}
	spot := &RoutineBuildingPlanner{goal: policy.MaintainMedicalCare, definition: "SleepingSpot"}
	if missing, method, reason := spot.selection(facts); missing != 1 || method != "hospital-SleepingSpot" || reason != "" {
		t.Fatal(missing, method, reason)
	}
}
