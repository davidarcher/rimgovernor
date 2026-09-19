package store

import (
	"context"
	"encoding/json"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"reflect"
	"testing"
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
func TestRoutineDevelopmentPersistsAgeAndRechecksPlayerCapacity(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := routineRequest()
	r.Policy.MaxDevelopmentProjects = 1
	first := reviewRoutine(t, s, &r)
	row := developmentRow(t, first.Review, policy.MaintainWood)
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
	// An accepted player project consumes the slot before a new routine review.
	sub, _, err := s.SubmitBuilding(ctx, submissionRequest(t, "capacity"))
	if err != nil {
		t.Fatal(err)
	}
	g := routineGoal(t, first, policy.MaintainWood)
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "wood", plan(t, "wood", "wood-action")); !errors.Is(err, ErrNotAdmitted) {
		t.Fatal(err)
	}
	r.Tick = 2510
	second := reviewRoutine(t, s, &r)
	row = developmentRow(t, second.Review, policy.MaintainWood)
	if row.Selected || row.Reason != policy.DevelopmentCapacity || row.WaitingSince != 10 || len(second.Review.Development.Committed) != 1 {
		t.Fatal(second)
	}
	if _, err = s.Cancel(ctx, sub.Plan, sub.Action); err != nil {
		t.Fatal(err)
	}
	third := reviewRoutine(t, s, &r)
	row = developmentRow(t, third.Review, policy.MaintainWood)
	if !row.Selected || row.WaitingSince != 10 {
		t.Fatal(third)
	}
	g = routineGoal(t, third, policy.MaintainWood)
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "wood", plan(t, "wood", "wood-action")); err != nil {
		t.Fatal(err)
	}
	committed := reviewRoutine(t, s, &r)
	row = developmentRow(t, committed.Review, policy.MaintainWood)
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
	row := developmentRow(t, unknown.Review, policy.MaintainWood)
	if row.Selected || row.Reason != policy.DevelopmentWorkersUnknown || row.WaitingSince != first.Review.Tick {
		t.Fatal(unknown)
	}
	r.Facts.Workers = domain.Known(2)
	r.Current.Load = "reloaded"
	changed := reviewRoutine(t, s, &r)
	row = developmentRow(t, changed.Review, policy.MaintainWood)
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

func TestRoutineDevelopmentCountsCancelledUncertainPlayerWork(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := routineRequest()
	r.Policy.MaxDevelopmentProjects = 1
	request := submissionRequest(t, "uncertain")
	sub, _, err := s.SubmitBuilding(ctx, request)
	if err != nil {
		t.Fatal(err)
	}
	snapshot := r.Current
	snapshot.Plan = sub.Plan
	snapshot.Revision = sub.Revision
	if _, err = s.ReserveAndPrepare(ctx, sub.Plan, sub.Action, Admission{Snapshot: snapshot, Tick: 10, Costs: []MaterialCost{{Definition: "WoodLog", Count: 5}}, Footprint: []domain.Cell{request.Building.Cell()}}); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, sub.Plan, sub.Action, snapshot, 10); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Cancel(ctx, sub.Plan, sub.Action); err != nil {
		t.Fatal(err)
	}
	r.Tick = 11
	held := reviewRoutine(t, s, &r)
	if row := developmentRow(t, held.Review, policy.MaintainWood); row.Selected || row.Reason != policy.DevelopmentCapacity {
		t.Fatal(held)
	}
	if _, err = s.Observe(ctx, sub.Plan, domain.Observation{Action: sub.Action, Attempt: 1, Snapshot: snapshot, Tick: 12, Effect: domain.EffectAbsent}, snapshot); err != nil {
		t.Fatal(err)
	}
	r.Tick = 13
	released := reviewRoutine(t, s, &r)
	if !developmentRow(t, released.Review, policy.MaintainWood).Selected {
		t.Fatal(released)
	}
}

// Configured research/resource targets rank with a measured deficit, and a
// production policy push is admitted without holding a development slot.
func TestRoutineDevelopmentConfiguredTargetsAndExemptPush(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := routineRequest()
	r.Policy.MaxDevelopmentProjects = 1
	r.Policy.ResearchTarget = "Stonecutting"
	r.Policy.ResourceTargets = map[policy.Resource]int64{"Steel": 100}
	r.Policy.ResourceReserves = map[policy.Resource]int64{"WoodLog": 50}
	r.Facts.Research = domain.Known(policy.ResearchFacts{Projects: []policy.ResearchProjectID{"Stonecutting"}})
	r.Facts.Resources = domain.Known([]policy.Amount{{Resource: "Steel", Count: 50}})
	out := reviewRoutine(t, s, &r)
	research := developmentRow(t, out.Review, policy.EnsureResearch)
	resource := developmentRow(t, out.Review, policy.MaintainResource)
	if research.Deficit == nil || *research.Deficit != 1 || resource.Deficit == nil || *resource.Deficit != 0.5 {
		t.Fatal(research, resource)
	}
	if !research.Selected || resource.Selected || resource.Reason != policy.DevelopmentCapacity {
		t.Fatal(research, resource)
	}
	for _, row := range out.Review.Development.Rows {
		if row.Goal == policy.ProductionPolicy {
			t.Fatal("configuration push must not rank for development")
		}
	}
	g := routineGoal(t, out, policy.ProductionPolicy)
	if g.Goal.Need != domain.NeedDeficit {
		t.Fatal(g.Goal)
	}
	if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "push", plan(t, "push", "push-action")); err != nil {
		t.Fatal("exempt push refused", err)
	}
	g = routineGoal(t, out, policy.MaintainResource)
	if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "steel", plan(t, "steel", "steel-action")); !errors.Is(err, ErrNotAdmitted) {
		t.Fatal("unselected resource goal admitted", err)
	}
	// Building admission reports the missing slot as a refusal reason, not
	// an error the planner would retry every step (#100).
	d, err := s.AdmitBuildingMethod(ctx, methodRequest(t, g, "sw", 10))
	if err != nil || d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason != policy.NoDevelopmentSlot {
		t.Fatal("unselected resource goal building admission", d, err)
	}
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
	// Recovered facts retire the goals; missing facts leave them unknown.
	r.Facts.Research = domain.Known(policy.ResearchFacts{Current: "Stonecutting", Projects: []policy.ResearchProjectID{"Stonecutting"}})
	r.Facts.Resources = domain.Known([]policy.Amount{{Resource: "Steel", Count: 120}})
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.EnsureResearch).Goal.Need != domain.NeedRecovered || routineGoal(t, out, policy.MaintainResource).Goal.Need != domain.NeedRecovered {
		t.Fatal(out.Goals)
	}
	r.Facts.Research, r.Facts.Resources = domain.Unknown[policy.ResearchFacts](), domain.Unknown[[]policy.Amount]()
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.EnsureResearch).Goal.Need != domain.NeedUnknown || routineGoal(t, out, policy.MaintainResource).Goal.Need != domain.NeedUnknown {
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
	r.Policy.MaxDevelopmentProjects = 4
	r.Policy.ResearchTarget = "Stonecutting"
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
	r.Facts.Colonists, r.Facts.IndoorCapacity = domain.Known(int64(3)), domain.Known(int64(2))
	second := reviewRoutine(t, s, &r)
	expansion := developmentRow(t, second.Review, policy.EnsureExpansion)
	if expansion.Selected || expansion.Reason != policy.DevelopmentLabor || expansion.Bottleneck != policy.WorkConstruction {
		t.Fatal(expansion)
	}
	if !developmentRow(t, second.Review, policy.EnsureResearch).Selected || !developmentRow(t, second.Review, policy.MaintainWood).Selected {
		t.Fatal(second.Review.Development.Rows)
	}
	g := routineGoal(t, second, policy.EnsureExpansion)
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "wall", plan(t, "wall", "wall-action")); !errors.Is(err, ErrNotAdmitted) {
		t.Fatal("labor-deferred goal admitted", err)
	}
	// An observed outdoor hazard defers outdoor work ahead of labor accounting
	// and the measured risk persists with the row.
	r.Facts.DisasterConditions = domain.Known([]policy.DisasterCondition{{ID: "1", Definition: "ToxicFallout"}})
	third := reviewRoutine(t, s, &r)
	expansion = developmentRow(t, third.Review, policy.EnsureExpansion)
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

// The review marks a startup goal served once a method is on record, and
// comfort's development row leaves startup_survival on the next ranking
// (#196: EnsureComfort sat refused for two in-game days behind startup goals
// whose fields, campfire and storage were already placed).
func TestRoutineDevelopmentComfortFollowsServedStartupGoals(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := routineRequest()
	r.Facts.Colonists, r.Facts.HousingTarget, r.Facts.BedCapacity, r.Facts.IndoorCapacity = domain.Known(int64(3)), domain.Known(int64(0)), domain.Known(int64(3)), domain.Known(int64(4))
	r.Facts.GrowingCells, r.Facts.Armed = domain.Known(int64(30)), domain.Known(int64(2))
	r.Facts.FoodDays, r.Facts.FieldCoverage, r.Facts.SleepingMin, r.Facts.SleepingMax = domain.Known(8.0), domain.Known(1.0), domain.Known(20.0), domain.Known(20.0)
	r.Facts.SleepingRecovered, r.Facts.ForbiddenSupplies, r.Facts.WorkCoverage = domain.Known(true), domain.Known(false), domain.Known(true)
	r.Facts.FoodStorage, r.Facts.PowerRequired, r.Facts.DisabledConsumers, r.Facts.MedicalCareRecovered = domain.Known(true), domain.Known(false), domain.Known(false), domain.Known(true)
	r.Facts.FoodStorageUpkeep = policy.FoodStorageObservation{Stocks: domain.Known([]policy.FoodStorageStock{})}
	r.Facts.Cooking = domain.Known(false)
	r.Facts.Comfort = domain.Known(comfortCensus(false))
	r.Facts.BasicComfort = domain.Known(comfortCensus(false))
	out := reviewRoutine(t, s, &r)
	if row := developmentRow(t, out.Review, policy.EnsureComfort); row.Selected || row.Reason != policy.DevelopmentStartup {
		t.Fatal(row)
	}
	cooking := routineGoal(t, out, policy.EnsureCooking)
	if cooking.Goal.Need != domain.NeedDeficit {
		t.Fatal(cooking)
	}
	if _, err := s.CommitGoalMethod(ctx, cooking.Goal.ID, cooking.Revision, "campfire", plan(t, "campfire", "campfire-action")); err != nil {
		t.Fatal(err)
	}
	out = reviewRoutine(t, s, &r)
	if row := developmentRow(t, out.Review, policy.EnsureComfort); !row.Selected || row.Reason != "" {
		t.Fatal(row, out.Review.Development.Rows)
	}
	// A settled method retires its plan (a completed campfire leaves the
	// active catalog) but the goal stays served.
	if _, err := s.Cancel(ctx, "campfire", "campfire-action"); err != nil {
		t.Fatal(err)
	}
	reviewRoutine(t, s, &r)
	out = reviewRoutine(t, s, &r)
	if cooking = routineGoal(t, out, policy.EnsureCooking); len(cooking.Methods) != 0 {
		t.Fatal("campfire method not retired", cooking.Methods)
	}
	if row := developmentRow(t, out.Review, policy.EnsureComfort); !row.Selected || row.Reason != "" {
		t.Fatal("retired startup method counted as unserved", row)
	}
}

// A selected goal that committed nothing by the next review is recorded
// idle and hands its slot to the next eligible goal; the flag survives the
// review record so the ranking sees it (colony-3 held both slots for a game
// day on goals whose planners had no method).
func TestRoutineDevelopmentIdleSelectionRotates(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := routineRequest()
	r.Policy.MaxDevelopmentProjects = 1
	r.Policy.ResearchTarget = "Stonecutting"
	r.Policy.ResourceTargets = map[policy.Resource]int64{"Steel": 100}
	r.Policy.ResourceReserves = map[policy.Resource]int64{"WoodLog": 50}
	r.Facts.Research = domain.Known(policy.ResearchFacts{Projects: []policy.ResearchProjectID{"Stonecutting"}})
	r.Facts.Resources = domain.Known([]policy.Amount{{Resource: "Steel", Count: 50}})
	first := reviewRoutine(t, s, &r)
	var selected, waiting []domain.GoalID
	for _, row := range first.Review.Development.Rows {
		switch {
		case row.Selected:
			selected = append(selected, row.Goal)
		case row.Reason == policy.DevelopmentCapacity:
			waiting = append(waiting, row.Goal)
		}
	}
	if len(selected) == 0 || len(waiting) == 0 {
		t.Fatal("fixture needs a selected goal and one waiting on capacity", first.Review.Development.Rows)
	}
	second := reviewRoutine(t, s, &r)
	for _, goal := range selected {
		row := developmentRow(t, second.Review, goal)
		if row.Selected || !row.Idle || row.WaitingSince != first.Review.Tick {
			t.Fatalf("%s should be idle with its age intact: %+v", goal, row)
		}
	}
	if row := developmentRow(t, second.Review, waiting[0]); !row.Selected {
		t.Fatalf("%s should take the idle slot: %+v", waiting[0], second.Review.Development.Rows)
	}
	loaded, err := s.LoadRoutineReview(context.Background())
	if err != nil || !developmentRow(t, loaded, selected[0]).Idle {
		t.Fatal("idle flag not persisted", err)
	}
}

func TestRoutineDevelopmentYieldMovesSlotWithinReview(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := routineRequest()
	r.Policy.MaxDevelopmentProjects = 1
	r.Policy.ResearchTarget = "Stonecutting"
	r.Policy.ResourceTargets = map[policy.Resource]int64{"Steel": 100}
	r.Policy.ResourceReserves = map[policy.Resource]int64{"WoodLog": 50}
	r.Facts.Research = domain.Known(policy.ResearchFacts{Projects: []policy.ResearchProjectID{"Stonecutting"}})
	r.Facts.Resources = domain.Known([]policy.Amount{{Resource: "Steel", Count: 50}})
	first := reviewRoutine(t, s, &r)
	var selected, waiting []domain.GoalID
	for _, row := range first.Review.Development.Rows {
		switch {
		case row.Selected:
			selected = append(selected, row.Goal)
		case row.Reason == policy.DevelopmentCapacity:
			waiting = append(waiting, row.Goal)
		}
	}
	if len(selected) != 1 || len(waiting) == 0 {
		t.Fatal("fixture needs one selected goal and one waiting on capacity", first.Review.Development.Rows)
	}
	// A stale revision cannot rewrite the current review.
	if _, err := s.YieldRoutineDevelopment(ctx, first.Review.Revision+1, selected[0]); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	yielded, err := s.YieldRoutineDevelopment(ctx, first.Review.Revision, selected[0])
	if err != nil || yielded.Revision != first.Review.Revision {
		t.Fatal(yielded, err)
	}
	if row := developmentRow(t, yielded, selected[0]); row.Selected || row.Reason != policy.DevelopmentMethodUnavailable || !row.Idle || row.WaitingSince != first.Review.Tick {
		t.Fatalf("yielder should read method_unavailable, idle, age intact: %+v", row)
	}
	if row := developmentRow(t, yielded, waiting[0]); !row.Selected || !row.Granted || row.Reason != "" {
		t.Fatalf("%s should take the yielded slot: %+v", waiting[0], yielded.Development.Rows)
	}
	loaded, err := s.LoadRoutineReview(ctx)
	if err != nil || !reflect.DeepEqual(loaded, yielded) {
		t.Fatal("yield not persisted", err)
	}
	// The recipient's method admits under the yielded selection; the
	// yielder's no longer does.
	recipient := routineGoal(t, first, waiting[0])
	if _, err = s.CommitGoalMethod(ctx, recipient.Goal.ID, recipient.Revision, "granted", plan(t, "granted", "granted-action")); err != nil {
		t.Fatal(err)
	}
	yielder := routineGoal(t, first, selected[0])
	if _, err = s.CommitGoalMethod(ctx, yielder.Goal.ID, yielder.Revision, "yielded", plan(t, "yielded", "yielded-action")); !errors.Is(err, ErrNotAdmitted) {
		t.Fatal(err)
	}
	// Yielding again is a no-op: the goal is no longer selected.
	again, err := s.YieldRoutineDevelopment(ctx, first.Review.Revision, selected[0])
	if err != nil || !reflect.DeepEqual(again, yielded) {
		t.Fatal(again, err)
	}
	// The next review keeps the recipient committed and ranks the yielder
	// idle rather than re-selecting it on hysteresis.
	r.Tick += 10
	second := reviewRoutine(t, s, &r)
	if row := developmentRow(t, second.Review, waiting[0]); !row.Committed || row.Granted {
		t.Fatalf("recipient should be committed: %+v", row)
	}
	if row := developmentRow(t, second.Review, selected[0]); row.Selected || !row.Idle || row.WaitingSince != first.Review.Tick {
		t.Fatalf("yielder should be idle with its age intact: %+v", row)
	}
}

func TestRoutineDevelopmentGrantedSelectionIsNotJudgedIdle(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := routineRequest()
	r.Policy.MaxDevelopmentProjects = 1
	r.Policy.ResearchTarget = "Stonecutting"
	r.Policy.ResourceTargets = map[policy.Resource]int64{"Steel": 100}
	r.Policy.ResourceReserves = map[policy.Resource]int64{"WoodLog": 50}
	r.Facts.Research = domain.Known(policy.ResearchFacts{Projects: []policy.ResearchProjectID{"Stonecutting"}})
	r.Facts.Resources = domain.Known([]policy.Amount{{Resource: "Steel", Count: 50}})
	first := reviewRoutine(t, s, &r)
	var selected, waiting []domain.GoalID
	for _, row := range first.Review.Development.Rows {
		switch {
		case row.Selected:
			selected = append(selected, row.Goal)
		case row.Reason == policy.DevelopmentCapacity:
			waiting = append(waiting, row.Goal)
		}
	}
	if len(selected) != 1 || len(waiting) == 0 {
		t.Fatal("fixture needs one selected goal and one waiting on capacity", first.Review.Development.Rows)
	}
	if _, err := s.YieldRoutineDevelopment(ctx, first.Review.Revision, selected[0]); err != nil {
		t.Fatal(err)
	}
	// The recipient's planner may never have run under the grant: the next
	// review keeps it selected (hysteresis) instead of demoting it idle.
	r.Tick += 10
	second := reviewRoutine(t, s, &r)
	if row := developmentRow(t, second.Review, waiting[0]); !row.Selected || row.Idle || row.Granted {
		t.Fatalf("granted goal should hold its slot through the next review: %+v", row)
	}
	if row := developmentRow(t, second.Review, selected[0]); row.Selected || !row.Idle {
		t.Fatalf("yielder should be idle: %+v", row)
	}
}

// An Equip of a weapon the colony already owns is no development project
// (#411): with both slots held by MaintainWood and MaintainMedicalReserves,
// EnsureBasicDefense's equip method is admitted anyway, and the open equip
// plan holds no slot of its own, while a building method of the same goal
// still needs the slot.
func TestRoutineDevelopmentEquipHoldsNoSlot(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := routineRequest()
	r.Policy.MaxDevelopmentProjects = 1
	out := reviewRoutine(t, s, &r)
	wood := routineGoal(t, out, policy.MaintainWood)
	if !developmentRow(t, out.Review, policy.MaintainWood).Selected {
		t.Fatal(out.Review.Development)
	}
	if _, err := s.CommitGoalMethod(ctx, wood.Goal.ID, wood.Revision, "wood", plan(t, "wood", "wood-action")); err != nil {
		t.Fatal(err)
	}
	// The defense deficit appears while the wood method holds the only slot.
	r.Facts.Colonists = domain.Known(int64(3))
	r.Facts.Armed = domain.Known(int64(0))
	out = reviewRoutine(t, s, &r)
	row := developmentRow(t, out.Review, policy.EnsureBasicDefense)
	if row.Selected || row.Reason != policy.DevelopmentCapacity || len(out.Review.Development.Committed) != 1 {
		t.Fatal(out.Review.Development)
	}
	defense := routineGoal(t, out, policy.EnsureBasicDefense)
	if _, err := s.CommitGoalMethod(ctx, defense.Goal.ID, defense.Revision, "sandbags", plan(t, "sandbags", "sandbags-action")); !errors.Is(err, ErrNotAdmitted) {
		t.Fatal("unselected defense building method admitted", err)
	}
	equip, _ := domain.NewEquip("unarmed", "Thing_Bow_Short5164", "Bow_Short", domain.Cell{X: 108, Z: 121})
	equipAction, err := domain.NewEquipAction("equip-action", equip)
	if err != nil {
		t.Fatal(err)
	}
	equipPlan, err := domain.NewPlan("equip", 1, []domain.Action{equipAction})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := s.CommitGoalMethod(ctx, defense.Goal.ID, defense.Revision, "equip", equipPlan); err != nil {
		t.Fatal("equip refused for a development slot", err)
	}
	out = reviewRoutine(t, s, &r)
	row = developmentRow(t, out.Review, policy.EnsureBasicDefense)
	if row.Committed || row.Reason != policy.DevelopmentCapacity || len(out.Review.Development.Committed) != 1 || out.Review.Development.Committed[0] != policy.MaintainWood {
		t.Fatal("open equip plan counted as a commitment", out.Review.Development)
	}
}
