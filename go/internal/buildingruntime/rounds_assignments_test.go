package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// Governor owns every priority: a player who switched the only
// builder's Construction off gets it switched back on.
func TestWorkPlannerRestoresPlayerDisabledWork(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, db, session, _, n := roundsFixture(t)
	r.native = &healthyWorkNative{roundsMedicalNative: &roundsMedicalNative{roundsNative: n}}
	v := n.reply.GetObserved()
	v.ColonistCount = proto.Uint32(1)
	v.WorkerCount = proto.Uint32(1)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("patient"), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	for _, skill := range []string{"Construction", "Plants", "Cooking", "Medicine", "Shooting"} {
		row.Biography.Skills = append(row.Biography.Skills, &o.Skill{DefName: proto.String(skill), Level: proto.Int32(10), Disabled: proto.Bool(false), Passion: o.Passion_PASSION_NONE.Enum()})
	}
	row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String("Construction"), Priority: proto.Int32(0), Disabled: proto.Bool(false)})
	for _, work := range []string{"Growing", "Cooking", "Doctor", "PlantCutting", "Firefighter"} {
		row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String(work), Priority: proto.Int32(1), Disabled: proto.Bool(false)})
	}
	row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String("Hunting"), Priority: proto.Int32(0), Disabled: proto.Bool(false)})
	n.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{row}, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}}}

	ctx := context.Background()
	if _, err := r.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoundsWorkPlanner(r)
	if err != nil {
		t.Fatal(err)
	}
	before := session.acquires.Load()
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 1 || session.acquires.Load() != before {
		t.Fatal(plan, err)
	}
	work, ok := plan.Spec.Actions()[0].WorkAssignment()
	restored := false
	for _, s := range work.Settings() {
		restored = restored || s.Definition == "Construction" && s.Priority > 0
	}
	if !ok || work.Pawn() != "patient" || !restored {
		t.Fatal(work)
	}
	if next, err := planner.Step(ctx); err != nil || next.Verdict != BuildingReasonExistingWork {
		t.Fatal(next, err)
	}
}

// A pawn whose readback still says checkbox mode (native has not flipped
// numbered priorities on yet) is unknown this round: no write.
func TestWorkPlannerSkipsPawnInCheckboxMode(t *testing.T) {
	t.Parallel()
	r, db, _, _, n := roundsFixture(t)
	r.native = &healthyWorkNative{roundsMedicalNative: &roundsMedicalNative{roundsNative: n}}
	v := n.reply.GetObserved()
	v.ColonistCount = proto.Uint32(1)
	v.WorkerCount = proto.Uint32(1)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("patient"), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(false)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	for _, skill := range []string{"Construction", "Plants", "Cooking", "Medicine", "Shooting"} {
		row.Biography.Skills = append(row.Biography.Skills, &o.Skill{DefName: proto.String(skill), Level: proto.Int32(10), Disabled: proto.Bool(false), Passion: o.Passion_PASSION_NONE.Enum()})
	}
	for _, work := range []string{"Growing", "Cooking", "Doctor", "PlantCutting", "Firefighter"} {
		row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String(work), Priority: proto.Int32(3), Disabled: proto.Bool(false)})
	}
	// Construction is unchecked although the pawn is the only builder.
	row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String("Construction"), Priority: proto.Int32(0), Disabled: proto.Bool(false)})
	n.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{row}, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}}}

	ctx := context.Background()
	if _, err := r.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoundsWorkPlanner(r)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict == BuildingReasonAdmitted || result.Plan != "" {
		t.Fatal(result, err)
	}
	_ = db
}

// A pending work assignment whose premise moved is cancelled by the next
// planner step instead of gating re-planning forever: the armed
// shooter's Hunting proposal is dropped once the pawn disarms.
func TestWorkPlannerCancelsStalePendingAssignments(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, db, _, _, n := roundsFixture(t)
	r.native = &healthyWorkNative{roundsMedicalNative: &roundsMedicalNative{roundsNative: n}}
	v := n.reply.GetObserved()
	v.ColonistCount = proto.Uint32(1)
	v.WorkerCount = proto.Uint32(1)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	bow := &o.PawnEquipment{Armed: proto.Bool(true), PrimaryId: proto.String("bow"), Equipped: []*o.GearItem{{Thing: n.entity(&o.EntityRef{Id: proto.String("bow"), DefName: proto.String("Bow_Short")})}}}
	row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("patient"), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: bow, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	for _, skill := range []string{"Construction", "Plants", "Cooking", "Medicine", "Shooting"} {
		row.Biography.Skills = append(row.Biography.Skills, &o.Skill{DefName: proto.String(skill), Level: proto.Int32(10), Disabled: proto.Bool(false), Passion: o.Passion_PASSION_NONE.Enum()})
	}
	for _, work := range []string{"Growing", "Cooking", "Doctor", "PlantCutting", "Firefighter"} {
		row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String(work), Priority: proto.Int32(1), Disabled: proto.Bool(false)})
	}
	// The only builder and the only shooter has both work types off.
	for _, work := range []string{"Construction", "Hunting"} {
		row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String(work), Priority: proto.Int32(0), Disabled: proto.Bool(false)})
	}
	n.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{row}, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}}}

	ctx := context.Background()
	planner, err := NewRoundsWorkPlanner(r)
	if err != nil {
		t.Fatal(err)
	}
	settings := func(id domain.PlanID) map[string]int32 {
		t.Helper()
		plan, err := db.LoadPlan(ctx, id)
		if err != nil || len(plan.Progress) != 1 {
			t.Fatal(plan, err)
		}
		work, ok := plan.Spec.Actions()[0].WorkAssignment()
		if !ok {
			t.Fatal(plan)
		}
		values := map[string]int32{}
		for _, setting := range work.Settings() {
			values[setting.Definition] = setting.Priority
		}
		return values
	}
	stage := func(id domain.PlanID) domain.Stage {
		t.Helper()
		plan, err := db.LoadPlan(ctx, id)
		if err != nil {
			t.Fatal(err)
		}
		return plan.Progress[0].View().Stage
	}
	if _, err = r.Step(ctx); err != nil {
		t.Fatal(err)
	}
	first, err := planner.Step(ctx)
	if err != nil || first.Verdict != BuildingReasonAdmitted {
		t.Fatal(first, err)
	}
	if values := settings(first.Plan); len(values) != 2 || values["Construction"] == 0 || values["Hunting"] == 0 {
		t.Fatal(values)
	}
	// Nothing moved: the proposal stays open and gates a second plan.
	if next, err := planner.Step(ctx); err != nil || next.Verdict != BuildingReasonExistingWork {
		t.Fatal(next, err)
	}
	// The pawn disarmed before the write ran: the fresh decision no longer
	// wants Hunting on, so the pending action is cancelled and a plan for
	// Construction alone replaces it.
	row.Equipment = &o.PawnEquipment{Armed: proto.Bool(false)}
	if _, err = r.Step(ctx); err != nil {
		t.Fatal(err)
	}
	second, err := planner.Step(ctx)
	if err != nil || second.Verdict != BuildingReasonAdmitted || second.Plan == first.Plan {
		t.Fatal(second, err)
	}
	if stage(first.Plan) != domain.Cancelled {
		t.Fatal(stage(first.Plan))
	}
	if values := settings(second.Plan); len(values) != 1 || values["Construction"] == 0 {
		t.Fatal(values)
	}
	// Re-armed: the fresh decision still agrees with the pending
	// Construction write, so it stays open rather than churning.
	row.Equipment = bow
	if _, err = r.Step(ctx); err != nil {
		t.Fatal(err)
	}
	if next, err := planner.Step(ctx); err != nil || next.Verdict != BuildingReasonExistingWork || stage(second.Plan) != domain.Pending {
		t.Fatal(next, err)
	}
}

type healthyWorkNative struct{ *roundsMedicalNative }

func (n *healthyWorkNative) ReadEmergency(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	v, r, e := n.roundsMedicalNative.ReadEmergency(ctx, id)
	v.Facts.Colonists[0].NeedsTend = domain.Known(false)
	return v, r, e
}

func (n *healthyWorkNative) ReadRoundsFrame(ctx context.Context, id *c.Identity) (bridge.RoundsFrame, error) {
	return fakeFrame(ctx, n, id)
}
