package store

import (
	"context"
	"encoding/json"
	"errors"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func developmentRow(t *testing.T, r RoutineReview, id domain.GoalID) RoutineDevelopmentRow {
	t.Helper()
	for _, row := range r.Development.Rows {
		if row.Goal == id {
			return row
		}
	}
	t.Fatal("missing development row", id)
	return RoutineDevelopmentRow{}
}
func TestRoutineDevelopmentPersistsAge(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := routineRequest()
	first := reviewRoutine(t, s, &r)
	row := developmentRow(t, first.Review, policy.MaintainResource)
	if !row.Selected || row.Deficit == nil || *row.Deficit <= 0 || first.Review.Development.Workers == nil || *first.Review.Development.Workers != 2 {
		t.Fatal(first)
	}
	s.Close()
	s = open(t, path)
	defer s.Close()
	loaded, err := s.LoadRoutineReview(ctx)
	if err != nil || !reflect.DeepEqual(loaded, first.Review) {
		t.Fatal(loaded, err)
	}
	g := routineGoal(t, first, policy.MaintainResource)
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "wood", plan(t, "wood", "wood-action")); err != nil {
		t.Fatal(err)
	}
	committed := reviewRoutine(t, s, &r)
	row = developmentRow(t, committed.Review, policy.MaintainResource)
	if row.Selected || !row.Committed || row.Reason != policy.DevelopmentCommitted {
		t.Fatal(committed)
	}
	// A disabled review (Manual, an interruption, a restart before
	// authority returns) keeps the last ranking so waiting ages survive it.
	r.Enabled = false
	r.Tick += 500
	stopped := reviewRoutine(t, s, &r)
	if stopped.Review.Development.Tick != committed.Review.Tick || len(stopped.Review.Development.Rows) != len(committed.Review.Development.Rows) {
		t.Fatal(stopped)
	}
	for _, row := range stopped.Review.Development.Rows {
		if row.Selected || row.Reason == "" {
			t.Fatalf("disabled review left %s selected or unexplained", row.Goal)
		}
	}
	r.Enabled = true
	r.Tick += 500
	resumed := reviewRoutine(t, s, &r)
	for _, want := range committed.Review.Development.Rows {
		got := developmentRow(t, resumed.Review, want.Goal)
		if !want.Committed && !got.Committed && got.WaitingSince != want.WaitingSince {
			t.Fatalf("%s waiting age rewritten across a disabled review: %d -> %d", want.Goal, want.WaitingSince, got.WaitingSince)
		}
	}
}

func TestRoutineDevelopmentUnknownWorkersAndReloadReset(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := routineRequest()
	first := reviewRoutine(t, s, &r)
	r.Facts.Workers = domain.Unknown[int]()
	r.Tick = 2510
	unknown := reviewRoutine(t, s, &r)
	row := developmentRow(t, unknown.Review, policy.MaintainResource)
	if row.Selected || row.Reason != policy.DevelopmentWorkersUnknown || row.WaitingSince != first.Review.Tick {
		t.Fatal(unknown)
	}
	r.Facts.Workers = domain.Known(2)
	r.Current.Load = "reloaded"
	changed := reviewRoutine(t, s, &r)
	row = developmentRow(t, changed.Review, policy.MaintainResource)
	if row.WaitingSince != changed.Review.Tick || !row.Selected {
		t.Fatal(changed)
	}
}

func TestRoutineDevelopmentRejectsCorruptDurableSelections(t *testing.T) {
	t.Parallel()
	for _, fault := range []string{"workers", "capacity", "unknown-deficit", "goal", "scope"} {
		t.Run(fault, func(t *testing.T) {
			s := open(t, memoryPath(t))
			defer s.Close()
			r := routineRequest()
			record := reviewRoutine(t, s, &r).Review
			switch fault {
			case "workers":
				record.Development.Workers = nil
			case "capacity":
				record.Development.Capacity = 9
			case "unknown-deficit":
				for i := range record.Development.Rows {
					record.Development.Rows[i].Deficit = nil
				}
			case "goal":
				record.Development.Rows[0].Goal = "not-a-routine-need"
			case "scope":
				record.Development.Snapshot.Load = "other"
			}
			payload, err := json.Marshal(record)
			if err != nil {
				t.Fatal(err)
			}
			if _, err = s.db.Exec("UPDATE routine_review SET payload=?", payload); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadRoutineReview(context.Background()); err == nil {
				t.Fatal("corrupt development history accepted")
			}
		})
	}
}

// MaintainFoodStorage's priority depends on facts the reload does not have;
// a row it ranked as development must still load on resume.
func TestRoutineDevelopmentReloadsFoodStorageRow(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := routineRequest()
	record := reviewRoutine(t, s, &r).Review
	record.Development.Rows[0].Goal = policy.MaintainFoodStorage
	payload, err := json.Marshal(record)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.db.Exec("UPDATE routine_review SET payload=?", payload); err != nil {
		t.Fatal(err)
	}
	if _, err = s.LoadRoutineReview(context.Background()); err != nil {
		t.Fatal(err)
	}
}

// Configured research/resource targets rank with a measured deficit.
func TestRoutineDevelopmentConfiguredTargets(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := routineRequest()
	r.Policy.Stage.Floor = policy.StageDevelopment
	r.Policy.ResearchLadder = []string{"Stonecutting"}
	r.Policy.ResourceTargets = map[policy.Resource]int64{"Steel": 100}
	r.Facts.Research = domain.Known(policy.ResearchFacts{Projects: []policy.ResearchProjectID{"Stonecutting"}})
	r.Facts.Resources = domain.Known([]policy.Amount{{Resource: "Steel", Count: 50}})
	r.Facts.Wood = domain.Known(int64(400))
	out := reviewRoutine(t, s, &r)
	research := developmentRow(t, out.Review, policy.EnsureResearch)
	resource := developmentRow(t, out.Review, policy.MaintainResource)
	if research.Deficit == nil || *research.Deficit != 1 || resource.Deficit == nil || *resource.Deficit != 0.5 {
		t.Fatal(research, resource)
	}
	if !research.Selected || !resource.Selected {
		t.Fatal(research, resource)
	}
	g := routineGoal(t, out, policy.MaintainResource)
	// A method that is only a quest acceptance (#250) is a settings write
	// with no pawn work: it is admitted without the slot the same goal's
	// pawn-work methods still need.
	accept, _ := domain.NewQuestAccept("quest-1", "", -1)
	questAction, err := domain.NewQuestAcceptAction("accept-action", accept)
	if err != nil {
		t.Fatal(err)
	}
	questPlan, err := domain.NewPlan("accept", 1, []domain.Action{questAction})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "accept", questPlan); err != nil {
		t.Fatal("quest acceptance refused for a development slot", err)
	}
	// A wanderer letter uses the same no-pawn-work exemption, even though
	// its action is dispatched through the dialog-answer executor.
	if _, err := s.Cancel(ctx, questPlan.ID(), questAction.ID()); err != nil {
		t.Fatal(err)
	}
	g, err = s.LoadGoal(ctx, g.Goal.ID)
	if err != nil {
		t.Fatal(err)
	}
	letter, _ := domain.NewJoinerLetterAnswer(8, "Accept", "letter-token")
	letterAction, _ := domain.NewDialogAnswerAction("letter-action", letter)
	letterPlan, _ := domain.NewPlan("letter", 1, []domain.Action{letterAction})
	if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "letter", letterPlan); err != nil {
		t.Fatal("joiner letter refused for a development slot", err)
	}
	dialog, _ := domain.NewDialogAnswer(8, 0, "Accept")
	dialogAction, _ := domain.NewDialogAnswerAction("ordinary-dialog", dialog)
	dialogPlan, _ := domain.NewPlan("ordinary-dialog", 1, []domain.Action{dialogAction})
	if developmentExemptMethod(dialogPlan) {
		t.Fatal("ordinary dialog gained the population exemption")
	}
	// Recovered facts retire the goals; missing facts leave the resource
	// unknown (the research ladder is only walked under a known census).
	r.Facts.Research = domain.Known(policy.ResearchFacts{Current: "Stonecutting", Projects: []policy.ResearchProjectID{"Stonecutting"}})
	r.Facts.Resources = domain.Known([]policy.Amount{{Resource: "Steel", Count: 120}})
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.EnsureResearch).Goal.Need != domain.NeedRecovered || routineGoal(t, out, policy.MaintainResource).Goal.Need != domain.NeedRecovered {
		t.Fatal(out.Goals)
	}
	r.Facts.Research, r.Facts.Resources = domain.Unknown[policy.ResearchFacts](), domain.Unknown[[]policy.Amount]()
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.MaintainResource).Goal.Need != domain.NeedUnknown {
		t.Fatal(out.Goals)
	}
}

// A labor census persists with the ranking and reloads unchanged; a goal
// whose only work type is occupied by a committed player project defers
// with the bottleneck named rather than counting as capacity-deferred.
func TestRoutineDevelopmentLaborPersistsAndDefers(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := routineRequest()
	r.Policy.Stage.Floor = policy.StageDevelopment
	r.Policy.ResearchLadder = []string{"Stonecutting"}
	r.Facts.Research = domain.Known(policy.ResearchFacts{Projects: []policy.ResearchProjectID{"Stonecutting"}})
	r.Facts.Workers = domain.Known(4)
	r.Facts.Labor = domain.Known(map[policy.WorkType]int{policy.WorkConstruction: 1, policy.WorkResearch: 1, policy.WorkPlantCutting: 1})
	r.Facts.Colonists, r.Facts.IndoorCapacity, r.Facts.BedCapacity = domain.Known(int64(3)), domain.Known(int64(3)), domain.Known(int64(3))
	first := reviewRoutine(t, s, &r)
	if first.Review.Development.Labor[policy.WorkResearch] != 1 || developmentRow(t, first.Review, policy.EnsureResearch).Bottleneck != "" {
		t.Fatal(first.Review.Development)
	}
	s.Close()
	s = open(t, path)
	defer s.Close()
	loaded, err := s.LoadRoutineReview(ctx)
	if err != nil || !reflect.DeepEqual(loaded, first.Review) {
		t.Fatal(loaded, err)
	}
	if _, _, err = s.SubmitBuilding(ctx, submissionRequest(t, "builder")); err != nil {
		t.Fatal(err)
	}
	r.Facts.Colonists, r.Facts.IndoorCapacity = domain.Known(int64(3)), domain.Known(int64(3))
	second := reviewRoutine(t, s, &r)
	expansion := developmentRow(t, second.Review, policy.MaintainHousing)
	if expansion.Selected || expansion.Reason != policy.DevelopmentLabor || expansion.Bottleneck != policy.WorkConstruction {
		t.Fatal(expansion)
	}
	if !developmentRow(t, second.Review, policy.EnsureResearch).Selected || !developmentRow(t, second.Review, policy.MaintainResource).Selected {
		t.Fatal(second.Review.Development.Rows)
	}
	g := routineGoal(t, second, policy.MaintainHousing)
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "wall", plan(t, "wall", "wall-action")); !errors.Is(err, ErrNotAdmitted) {
		t.Fatal("labor-deferred goal admitted", err)
	}
	// An observed outdoor hazard defers outdoor work ahead of labor accounting
	// and the measured risk persists with the row.
	r.Facts.DisasterConditions = domain.Known([]policy.DisasterCondition{{ID: "1", Definition: "ToxicFallout"}})
	third := reviewRoutine(t, s, &r)
	expansion = developmentRow(t, third.Review, policy.MaintainHousing)
	if expansion.Reason != policy.DevelopmentRisk || expansion.Risk == nil || *expansion.Risk != 1 || expansion.Bottleneck != "" {
		t.Fatal(expansion)
	}
	if research := developmentRow(t, third.Review, policy.EnsureResearch); !research.Selected || research.Risk == nil || *research.Risk != 0 {
		t.Fatal(research)
	}
	loaded, err = s.LoadRoutineReview(ctx)
	if err != nil || !reflect.DeepEqual(loaded, third.Review) {
		t.Fatal(loaded, err)
	}
}

// A husbandry designation cancel is a settings write and holds no
// development slot (#577); a tame puts a handler to work and does.
func TestDevelopmentExemptHusbandrySettingsWrite(t *testing.T) {
	t.Parallel()
	cancel, _ := domain.NewHusbandry("Thing_Cow1", domain.HusbandryCancelSlaughter, "")
	cancelAction, _ := domain.NewHusbandryAction("cancel-action", cancel)
	cancelPlan, _ := domain.NewPlan("cancel", 1, []domain.Action{cancelAction})
	if !developmentExemptMethod(cancelPlan) {
		t.Fatal("cancel_slaughter needs a development slot")
	}
	tame, _ := domain.NewHusbandry("Thing_Muffalo1", domain.HusbandryTame, "")
	tameAction, _ := domain.NewHusbandryAction("tame-action", tame)
	tamePlan, _ := domain.NewPlan("tame", 1, []domain.Action{tameAction})
	if developmentExemptMethod(tamePlan) {
		t.Fatal("tame gained the settings-write exemption")
	}
}

// Zoning is an instant native write no pawn works: a stockpile zone
// creation holds no development slot, so a fresh colony's opening zones
// never wait behind the shelter for a slot or a hauler.
func TestDevelopmentExemptZoneCreate(t *testing.T) {
	t.Parallel()
	zone, err := domain.NewFilteredStockpileZone(domain.GeneralFilter(), domain.NormalPriority, []domain.Cell{{X: 1, Z: 1}})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewZoneCreateAction("zone-action", zone)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("zone", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if !developmentExemptMethod(plan) {
		t.Fatal("a zone creation needs a development slot")
	}
}

// The labor-idle deadline is durable game-tick history: it survives a store
// restart. Idle labor never releases the commitment: the dispatched action
// keeps its admission (material and cell claims) and its progress, and when
// labor returns the idle age clears with no second dispatch (#643).
func TestRoutineDevelopmentIdleAgeSurvivesRestartAndKeepsClaims(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := routineRequest()
	out := reviewRoutine(t, s, &r)
	wood := routineGoal(t, out, policy.MaintainResource)
	if _, err := s.CommitGoalMethod(ctx, wood.Goal.ID, wood.Revision, "wood", plan(t, "wood", "wood-action")); err != nil {
		t.Fatal(err)
	}
	const woodPlan domain.PlanID = "wood"
	woodScope := scope()
	woodScope.Plan = woodPlan
	claim := evidence(r.Tick, 40)
	claim.Snapshot = woodScope
	if _, err := s.Prepare(ctx, woodPlan, "wood-action", claim.Snapshot, claim.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, woodPlan, "wood-action", woodScope, r.Tick); err != nil {
		t.Fatal(err)
	}
	before, err := s.LoadPlan(ctx, woodPlan)
	if err != nil { // a building intent records no admission row (#856)
		t.Fatal(before, err)
	}
	r.Facts.Colonists = domain.Known(int64(3))
	r.Facts.Armed = domain.Known(int64(0))
	r.Facts.LaborUse = domain.Known(policy.LaborUse{Busy: map[policy.WorkType]int{policy.WorkConstruction: 1}, Idle: map[policy.WorkType]int{policy.WorkPlantCutting: 2}})
	r.Tick += 10
	since := r.Tick
	out = reviewRoutine(t, s, &r)
	if row := developmentRow(t, out.Review, policy.MaintainResource); !row.Committed || row.LaborIdleSince == nil || *row.LaborIdleSince != since {
		t.Fatal("first idle review", out.Review.Development)
	}

	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	r.Tick = since + policy.DevelopmentIdleTicks/2
	out = reviewRoutine(t, s, &r)
	if row := developmentRow(t, out.Review, policy.MaintainResource); !row.Committed || row.LaborIdleSince == nil || *row.LaborIdleSince != since {
		t.Fatal("restart moved the idle deadline", out.Review.Development)
	}
	r.Tick = since + policy.DevelopmentIdleTicks
	out = reviewRoutine(t, s, &r)
	if row := developmentRow(t, out.Review, policy.MaintainResource); !row.Committed || row.LaborIdleSince == nil || *row.LaborIdleSince != since {
		t.Fatal("idle labor past the restored deadline released the commitment", out.Review.Development)
	}
	released, err := s.LoadPlan(ctx, woodPlan)
	if err != nil || !reflect.DeepEqual(before, released) {
		t.Fatal("an idle review touched the action's progress or claims", err)
	}

	r.Facts.LaborUse = domain.Known(policy.LaborUse{Busy: map[policy.WorkType]int{policy.WorkPlantCutting: 1}, Idle: map[policy.WorkType]int{}})
	r.Tick += 10
	out = reviewRoutine(t, s, &r)
	if row := developmentRow(t, out.Review, policy.MaintainResource); !row.Committed || row.LaborIdleSince != nil {
		t.Fatal("resumed work should commit again", out.Review.Development)
	}
	resumed, err := s.LoadPlan(ctx, woodPlan)
	if err != nil || !reflect.DeepEqual(before, resumed) {
		t.Fatal("resumed work was dispatched again or lost its claims", err)
	}
}

// Work a goal admitted while it bypassed the ranked queue (priority 2, as
// MaintainFoodStorage does for a larder or reserve access) holds no slot
// after the goal drops back to 3: the commitment keeps the priority it was
// admitted under, so the review never counts more slots than capacity
// (#705).
func TestRoutineDevelopmentBypassAdmissionHoldsNoSlot(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := routineRequest()
	out := reviewRoutine(t, s, &r)
	wood := routineGoal(t, out, policy.MaintainResource)
	if _, err := s.CommitGoalMethod(ctx, wood.Goal.ID, wood.Revision, "wood", plan(t, "wood", "wood-action")); err != nil {
		t.Fatal(err)
	}
	r.Facts.Colonists = domain.Known(int64(3))
	r.Facts.Armed = domain.Known(int64(0))
	out = reviewRoutine(t, s, &r)
	if len(out.Review.Development.Committed) != 1 || !developmentRow(t, out.Review, policy.EnsureBasicDefense).Selected {
		t.Fatal("slot work admitted at priority 3 holds the slot", out.Review.Development)
	}
	if _, err := s.db.ExecContext(ctx, "UPDATE goal_methods SET priority=2 WHERE plan_id='wood'"); err != nil {
		t.Fatal(err)
	}
	out = reviewRoutine(t, s, &r)
	if len(out.Review.Development.Committed) != 0 || !developmentRow(t, out.Review, policy.EnsureBasicDefense).Selected {
		t.Fatal("work admitted at priority 2 counted as a slot", out.Review.Development)
	}
}
