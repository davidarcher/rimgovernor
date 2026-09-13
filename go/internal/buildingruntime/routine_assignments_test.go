package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestWorkPlannerAppliesSavedOverrideAndInvalidatesOnPreferenceChange(t *testing.T) {
	t.Parallel()
	r, db, session, _, n := routineFixture(t)
	r.native = &healthyWorkNative{routineMedicalNative: &routineMedicalNative{routineNative: n}}
	v := n.reply.GetObserved()
	v.ColonistCount = proto.Uint32(1)
	v.WorkerCount = proto.Uint32(1)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	v.Issues = append(v.Issues, missing("naming"))
	row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("patient"), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	for _, skill := range []string{"Construction", "Plants", "Cooking", "Medicine", "Shooting"} {
		row.Biography.Skills = append(row.Biography.Skills, &o.Skill{Definition: &o.DefinitionRef{DefName: proto.String(skill)}, Level: proto.Int32(10), Disabled: proto.Bool(false), Passion: proto.String("None")})
	}
	for _, work := range []string{"Construction", "Growing", "Cooking", "Doctor", "PlantCutting", "Firefighter"} {
		row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String(work), Priority: proto.Int32(1), Disabled: proto.Bool(false)})
	}
	row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String("Hunting"), Priority: proto.Int32(0), Disabled: proto.Bool(false)})
	n.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{row}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}

	ctx := context.Background()
	row.Settings.Snapshot = &o.SnapshotRef{Context: proto.Clone(v.Context).(*c.ObservationContext), EntityId: proto.String("patient"), Token: proto.String("before-work")}
	snapshot := r.player.State().Snapshot
	saved, err := r.player.SetWorkPreferences(ctx, store.WorkPreferenceRequest{RequestID: "disable-builder", Plan: snapshot.Plan, World: playerWorld(snapshot), ExpectedRevision: 0, Overrides: []policy.WorkOverride{{Pawn: "patient", Work: "Construction", Priority: 0}}})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = r.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineWorkPlanner(r)
	if err != nil {
		t.Fatal(err)
	}
	before := session.acquires.Load()
	result, err := planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 1 || session.acquires.Load() != before {
		t.Fatal(plan, err)
	}
	work, ok := plan.Spec.Actions()[0].WorkAssignment()
	if !ok || work.Pawn() != "patient" || work.BeforeToken() != "before-work" || len(work.Settings()) != 1 || work.Settings()[0].Definition != "Construction" || work.Settings()[0].Priority != 0 {
		t.Fatal(work)
	}
	if next, err := planner.Step(ctx); err != nil || next.Reason != BuildingMethodExistingWork {
		t.Fatal(next, err)
	}
	if _, err = r.player.SetWorkPreferences(ctx, store.WorkPreferenceRequest{RequestID: "restore-builder", Plan: snapshot.Plan, World: playerWorld(snapshot), ExpectedRevision: saved.Preferences.Revision, Overrides: []policy.WorkOverride{}}); err != nil {
		t.Fatal(err)
	}
	plan, err = db.LoadPlan(ctx, result.Plan)
	if err != nil || plan.Progress[0].View().Stage != domain.Cancelled {
		t.Fatal(plan, err)
	}
}

type healthyWorkNative struct{ *routineMedicalNative }

func (n *healthyWorkNative) ReadEmergency(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	v, r, e := n.routineMedicalNative.ReadEmergency(ctx, id)
	v.Facts.Colonists[0].NeedsTend = domain.Known(false)
	return v, r, e
}
