package buildingruntime

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestAcquisitionPlannerBoundsWoodAndPreservesManual(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	reviewer, db, session, request, native := routineFixture(t)
	submitted := playerPlan(t, db)
	var err error
	for _, action := range submitted.Spec.Actions() {
		if _, err = db.Cancel(ctx, submitted.Spec.ID(), action.ID()); err != nil {
			t.Fatal(err)
		}
	}
	v := native.reply.GetObserved()
	v.PendingWoodUnits = proto.Float64(0)
	reviewer.native = &healthyWorkNative{routineMedicalNative: &routineMedicalNative{routineNative: native}}
	reviewer.methods = domain.Known([]policy.GoalID{policy.MaintainWood})
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
	native.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{row}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}

	for i := 0; i < 12; i++ {
		id := fmt.Sprint("plant", i)
		v.Acquisition = append(v.Acquisition, &o.AcquisitionFacts{Source: &o.EntityRef{Id: proto.String(id), DefName: proto.String("Oak"), MapId: v.Context.Identity.MapId, Position: proto.Clone(v.Center).(*c.Cell), Snapshot: &o.SnapshotRef{EntityId: proto.String(id), Token: proto.String("cas"), Context: proto.Clone(v.Context).(*c.ObservationContext)}}, Resource: proto.String("WoodLog"), Hunt: proto.Bool(false), Tree: proto.Bool(true), Food: proto.Bool(false), Designated: proto.Bool(false), Yield: proto.Float64(10), NutritionYield: proto.Float64(0)})
	}
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineAcquisitionPlanner(reviewer, policy.MaintainWood)
	if err != nil {
		t.Fatal(err)
	}
	before := session.acquires.Load()
	result, err := planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 8 || session.acquires.Load() != before {
		t.Fatal(plan, err)
	}
	root := session.State().Snapshot
	target := root
	target.Plan = plan.Spec.ID()
	target.Revision = plan.Spec.Revision()
	if err = db.AuthorizeRoutinePlan(ctx, root, target); err != nil {
		t.Fatal(err)
	}
	if work, _, err := clockSchedulerWork(plan, target); err != nil || !work {
		t.Fatal("ordinary harvest cannot advance", work, err)
	}
	if next, err := planner.Step(ctx); err != nil || next.Reason != BuildingMethodExistingWork {
		t.Fatal(next, err)
	}
	request.Kind, request.RequestID = store.PauseControl, "manual-acquisition"
	if _, err = reviewer.player.Pause(ctx, request); err != nil {
		t.Fatal(err)
	}
	plan, err = db.LoadPlan(ctx, result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	for _, progress := range plan.Progress {
		if progress.View().Stage != domain.Cancelled {
			t.Fatal(progress)
		}
	}
}

// dispatchedHunt builds a single dispatched, unresolved Hunt acquisition
// Progress, as native.HuntingSafety.RouteSafe leaves it while a hunter's
// route stays unsafe: prepared and dispatched, never observed.
func dispatchedHunt(t *testing.T, action domain.ActionID, thing string, tick domain.Tick) domain.Progress {
	t.Helper()
	value, err := domain.NewAcquisition(thing, "Deer", domain.Cell{X: 1, Z: 1})
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewAcquisitionAction(action, value)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan("hunt-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewProgress(spec, action)
	if err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Map: 1, Load: "load", Plan: spec.ID(), Revision: spec.Revision(), Native: 1}
	if p, err = p.Prepare(snapshot, tick); err != nil {
		t.Fatal(err)
	}
	if p, err = p.MarkDispatched(snapshot, tick); err != nil {
		t.Fatal(err)
	}
	return p
}

func TestStalledHuntActionsGracePeriod(t *testing.T) {
	t.Parallel()
	hunt := dispatchedHunt(t, "hunt-deer", "deer", 100)
	sources := map[string]bool{"deer": true}
	if got := stalledHuntActions([]domain.Progress{hunt}, sources, 100+5999, 6000); got != nil {
		t.Fatal("stalled before grace elapses", got)
	}
	got := stalledHuntActions([]domain.Progress{hunt}, sources, 100+6000, 6000)
	if len(got) != 1 || got[0] != "hunt-deer" {
		t.Fatal("did not report stalled hunt", got)
	}
	if got := stalledHuntActions([]domain.Progress{hunt}, sources, 100+6000, 0); got != nil {
		t.Fatal("zero grace must disable stall detection", got)
	}
}

func TestStalledHuntActionsIgnoresResolvedAndNonHuntWork(t *testing.T) {
	t.Parallel()
	hunt := dispatchedHunt(t, "hunt-deer", "deer", 100)
	sources := map[string]bool{"deer": true}
	if got := stalledHuntActions([]domain.Progress{hunt}, map[string]bool{}, 100+6000, 6000); got != nil {
		t.Fatal("unlisted source must not be treated as a stalled hunt", got)
	}
	resolved, err := hunt.Observe(domain.Observation{Action: hunt.View().Action, Attempt: hunt.View().Attempt, Snapshot: hunt.View().Snapshot, Tick: 100, Causality: domain.AfterDispatch, Effect: domain.EffectCompleted}, hunt.View().Snapshot)
	if err != nil {
		t.Fatal(err)
	}
	if got := stalledHuntActions([]domain.Progress{resolved}, sources, 100+6000, 6000); got != nil {
		t.Fatal("resolved hunt action must not be reported as stalled", got)
	}
}
