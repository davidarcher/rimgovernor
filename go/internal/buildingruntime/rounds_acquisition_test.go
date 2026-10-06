package buildingruntime

import (
	"context"
	"fmt"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestAcquisitionPlannerBoundsWoodAndPreservesManual(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	ctx := context.Background()
	reviewer, db, session, request, native := roundsFixture(t)
	submitted := playerPlan(t, db)
	var err error
	for _, action := range submitted.Spec.Actions() {
		if _, err = db.Cancel(ctx, submitted.Spec.ID(), action.ID()); err != nil {
			t.Fatal(err)
		}
	}
	v := native.reply.GetObserved()
	v.PendingWoodUnits = proto.Float64(0)
	reviewer.native = &healthyWorkNative{roundsMedicalNative: &roundsMedicalNative{roundsNative: native}}
	reviewer.methods = domain.Known([]policy.ConcernID{policy.MaintainResource})
	v.ColonistCount = proto.Uint32(1)
	v.WorkerCount = proto.Uint32(1)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	v.Issues = append(v.Issues, missing("naming"))
	row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("patient"), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	for _, skill := range []string{"Construction", "Plants", "Cooking", "Medicine", "Shooting"} {
		row.Biography.Skills = append(row.Biography.Skills, &o.Skill{DefName: proto.String(skill), Level: proto.Int32(10), Disabled: proto.Bool(false), Passion: o.Passion_PASSION_NONE.Enum()})
	}
	for _, work := range []string{"Construction", "Growing", "Cooking", "Doctor", "PlantCutting", "Firefighter"} {
		row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String(work), Priority: proto.Int32(1), Disabled: proto.Bool(false)})
	}
	row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String("Hunting"), Priority: proto.Int32(0), Disabled: proto.Bool(false)})
	native.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{row}, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}}}

	for i := 0; i < 12; i++ {
		id := fmt.Sprint("plant", i)
		v.Acquisition = append(v.Acquisition, &o.AcquisitionFacts{Taken: proto.Bool(false), Source: native.entity(&o.EntityRef{Id: proto.String(id), DefName: proto.String("Oak"), MapId: v.Context.Identity.MapId, Position: proto.Clone(v.Center).(*c.Cell)}), SourceSnapshot: &o.SnapshotRef{EntityId: proto.String(id), Token: proto.String("cas"), Context: proto.Clone(v.Context).(*c.ObservationContext)}, Resource: proto.String("WoodLog"), Hunt: proto.Bool(false), Tree: proto.Bool(true), Food: proto.Bool(false), Designated: proto.Bool(false), Yield: proto.Float64(10), NutritionYield: proto.Float64(0)})
	}
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoundsAcquisitionPlanner(reviewer, policy.MaintainResource)
	if err != nil {
		t.Fatal(err)
	}
	before := session.acquires.Load()
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
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
	if err = db.AuthorizeRoundsPlan(ctx, root, target); err != nil {
		t.Fatal(err)
	}
	if work, _, err := clockSchedulerWork(plan, target); err != nil || !work {
		t.Fatal("ordinary harvest cannot advance", work, err)
	}
	if next, err := planner.Step(ctx); err != nil || next.Verdict != BuildingReasonExistingWork {
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
		if progress.View().Stage != domain.Pending {
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

// MaintainResource chops, forages and hunts through the acquisition
// catalog (#728): a harvestable floor admits a bounded, nearest-first
// acquisition method on the goal, and open work holds the next one.
func TestResourceAcquisitionPlannerHarvestsForFloor(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	ctx := context.Background()
	reviewer, db, _, _, native := roundsFixture(t)
	submitted := playerPlan(t, db)
	var err error
	for _, action := range submitted.Spec.Actions() {
		if _, err = db.Cancel(ctx, submitted.Spec.ID(), action.ID()); err != nil {
			t.Fatal(err)
		}
	}
	v := native.reply.GetObserved()
	v.PendingWoodUnits = proto.Float64(0)
	reviewer.native = &healthyWorkNative{roundsMedicalNative: &roundsMedicalNative{roundsNative: native}}
	reviewer.methods = domain.Known([]policy.ConcernID{policy.MaintainResource})
	v.ColonistCount = proto.Uint32(1)
	v.WorkerCount = proto.Uint32(1)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	v.Issues = append(v.Issues, missing("naming"))
	row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("patient"), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	for _, skill := range []string{"Construction", "Plants", "Cooking", "Medicine", "Shooting"} {
		row.Biography.Skills = append(row.Biography.Skills, &o.Skill{DefName: proto.String(skill), Level: proto.Int32(10), Disabled: proto.Bool(false), Passion: o.Passion_PASSION_NONE.Enum()})
	}
	for _, work := range []string{"Construction", "Growing", "Cooking", "Doctor", "PlantCutting", "Firefighter"} {
		row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String(work), Priority: proto.Int32(1), Disabled: proto.Bool(false)})
	}
	row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String("Hunting"), Priority: proto.Int32(0), Disabled: proto.Bool(false)})
	native.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{row}, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}}}

	reviewer.policy.ResourceTargets = map[policy.Resource]int64{"Hay": 30}
	for i := 0; i < 6; i++ {
		id := fmt.Sprint("grass", i)
		cell := proto.Clone(v.Center).(*c.Cell)
		cell.X = proto.Int32(cell.GetX() + int32(i))
		v.Acquisition = append(v.Acquisition, &o.AcquisitionFacts{Taken: proto.Bool(false), Source: native.entity(&o.EntityRef{Id: proto.String(id), DefName: proto.String("Haygrass"), MapId: v.Context.Identity.MapId, Position: cell}), SourceSnapshot: &o.SnapshotRef{EntityId: proto.String(id), Token: proto.String("cas"), Context: proto.Clone(v.Context).(*c.ObservationContext)}, Resource: proto.String("Hay"), Hunt: proto.Bool(false), Tree: proto.Bool(false), Food: proto.Bool(false), Designated: proto.Bool(false), Yield: proto.Float64(10), NutritionYield: proto.Float64(0)})
	}
	// Berries have no target until the Hay method is admitted.
	for i := 0; i < 3; i++ {
		id := fmt.Sprint("bush", i)
		v.Acquisition = append(v.Acquisition, &o.AcquisitionFacts{Taken: proto.Bool(false), Source: native.entity(&o.EntityRef{Id: proto.String(id), DefName: proto.String("BerryPlant"), MapId: v.Context.Identity.MapId, Position: proto.Clone(v.Center).(*c.Cell)}), SourceSnapshot: &o.SnapshotRef{EntityId: proto.String(id), Token: proto.String("cas"), Context: proto.Clone(v.Context).(*c.ObservationContext)}, Resource: proto.String("RawBerries"), Hunt: proto.Bool(false), Tree: proto.Bool(false), Food: proto.Bool(false), Designated: proto.Bool(false), Yield: proto.Float64(10), NutritionYield: proto.Float64(0)})
	}
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoundsAcquisitionPlanner(reviewer, policy.MaintainResource)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 3 {
		t.Fatal(plan, err)
	}
	for i, p := range plan.Progress {
		if a, _ := p.Action().Acquisition(); a.Thing() != fmt.Sprint("grass", i) {
			t.Fatal("not nearest first", i, a.Thing())
		}
	}
	if next, err := planner.Step(ctx); err != nil || next.Verdict != BuildingReasonExistingWork {
		t.Fatal(next, err)
	}
	// Once dispatched the census takes over (#1045): the grass reads
	// designated and holds Hay with no open journal work behind it.
	for _, row := range v.Acquisition {
		row.Designated = proto.Bool(row.GetResource() == "Hay")
		if row.GetDesignated() {
			row.DesignatedTick = proto.Int64(0)
		}
	}
	for _, p := range plan.Progress {
		if _, err = db.Cancel(ctx, result.Plan, p.View().Action); err != nil {
			t.Fatal(err)
		}
	}
	if _, err = reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	if next, err := planner.Step(ctx); err != nil || next.Verdict != BuildingReasonExistingWork {
		t.Fatal(next, err)
	}
	// A designated resource skips only itself: a second floor still
	// plans, and never re-admits a held grass source.
	reviewer.policy.ResourceTargets["RawBerries"] = 30
	if _, err = reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	berries, err := planner.Step(ctx)
	if err != nil || berries.Verdict != BuildingReasonAdmitted {
		t.Fatal(berries, err)
	}
	if plan, err = db.LoadPlan(ctx, berries.Plan); err != nil || len(plan.Progress) == 0 {
		t.Fatal(plan, err)
	}
	for _, p := range plan.Progress {
		if a, _ := p.Action().Acquisition(); !strings.HasPrefix(a.Thing(), "bush") {
			t.Fatal("re-admitted a held source", a.Thing())
		}
	}
}
