package upkeep

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// upkeep/campaign (issue #99) is sustained survival on one colony: the
// deficits the single-deficit cases stage one at a time are chained on one
// kept world and one durable journal, and every deficit recovered earlier
// must stay closed while the later ones are handled. A stage stops the
// service, takes the slot back, stages its deficit over the live map (never
// a reload: the routine goals of the world survive a paired restart, so the
// journal still carries the earlier recoveries), serves again with every
// family composed so far, follows the stage's own journal watch and audits
// the native outcome. A recovered goal that is measured in deficit again
// starts a new method epoch (domain.ReviewGoal). Colony traffic can reopen
// a goal honestly (dirt tracked into the cleaned kitchen), so the property is
// the issue's: no deficit reopens without recovery. A reopen is recorded and
// the goal must be recovered again within reopenTicks and before the stage
// ends; a goal that is rebound, cancelled or invalidated fails at once.
//
//	kitchen  -- test/cleanliness_prepare (filthy): blood in an enclosed
//	            kitchen, every colonist's Cleaning at 0, so only
//	            MaintainCleanFacilities' forced orders clean it.
//	feed     -- test/feed_setup: a hungry confined pet, MaintainAnimalFeed.
//	            After the kitchen so its enclosed butchery bench stands as
//	            a lower-id decoy the kibble bill must not land on (#237).
//	medicine -- test/medicine_setup: no medicine, MaintainMedicalReserves.
//	cold     -- routine_sleeping_prepare + routine_temperature_prepare
//	            (coldSnap: days have passed, so ordinary cold snaps bring
//	            the afternoon under the campfire threshold),
//	            EnsureTemperatureSafety; last because a campfire's fuel is
//	            finite and its reopening would be the fixture's, not a
//	            regression.
//
// Calendar time is not the property: a season is 900k ticks, over an hour of
// wall time at Ultrafast, so a season soak stays a detached diagnostic
// outside cmd/test.
type stage struct {
	name     string
	fixture  string
	families []string
	// needs are the goals the stage must recover; every later stage asserts
	// they never reopen without recovering again.
	needs   []policy.GoalID
	prepare func(ctx context.Context, h *na.Harness, identity map[string]any, report na.Report) (map[string]any, error)
	watch   func(ctx context.Context, journal *store.Store, prepared map[string]any, report na.Report) error
	verify  func(ctx context.Context, h *na.Harness, identity, prepared map[string]any, report na.Report) error
}

// closedGoal is a goal an earlier stage recovered, pinned by identity and
// method epoch.
type closedGoal struct {
	stage string
	need  policy.GoalID
	goal  domain.GoalID
	epoch uint64
}

const campaignBudget = 25 * time.Minute

func campaignStages() []stage {
	all := scenarios()
	fromScenario := func(sc *scenario, needs ...policy.GoalID) stage {
		return stage{name: sc.name, fixture: sc.fixture, families: sc.families, needs: needs,
			prepare: sc.prepare, watch: sc.watch, verify: sc.verify}
	}
	stages := []stage{
		{name: "kitchen", fixture: "test/cleanliness_prepare", families: []string{"clean"},
			needs:   []policy.GoalID{policy.MaintainCleanFacilities},
			prepare: prepareKitchen, watch: watchKitchen, verify: verifyKitchen},
		fromScenario(all["feed"], policy.MaintainAnimalFeed),
		fromScenario(all["medicine"], policy.MaintainMedicalReserves),
		fromScenario(all["cold"], policy.EnsureTemperatureSafety),
	}
	// Days of play have passed by the last stage; the fixture cools the map
	// with ordinary cold snaps rather than failing on a warm afternoon.
	stages[3].prepare = prepareColdWith(true)
	return stages
}

func init() {
	stages := campaignStages()
	names := make([]string, 0, len(stages))
	for _, st := range stages {
		names = append(names, st.name)
	}
	cases.Register(cases.Case{
		Name: "upkeep/campaign",
		Scope: "Sustained colony upkeep (issue #99): on one kept " + sustained.BaselineSave + " colony and one durable journal the deficits " +
			fmt.Sprint(names) + " are staged in turn over the live map, each recovered by the routine families composed so far and audited natively, " +
			"and no goal an earlier stage recovered reopens (new method epoch), is rebound or is invalidated while the later ones are handled.",
		Start:  cases.Save{Name: sustained.BaselineSave},
		Serve:  &cases.ServeSpec{Families: cumulativeFamilies(stages, len(stages)), Extra: []string{"--routine-project-limit", "4"}, Prefix: prefix},
		Budget: campaignBudget,
		Reason: "Four deficits chained on one colony with a service restart between them; each stage alone runs in one to four minutes on the registry runner and the chain is the property under test.",
		Run:    runCampaign,
	})
}

// cumulativeFamilies is the union of the first n stages' families, in first
// appearance order: a stage's service reviews every earlier goal too.
func cumulativeFamilies(stages []stage, n int) []string {
	seen := map[string]bool{}
	var out []string
	for _, st := range stages[:n] {
		for _, family := range st.families {
			if !seen[family] {
				seen[family] = true
				out = append(out, family)
			}
		}
	}
	return out
}

func runCampaign(ctx context.Context, s cases.Session) error {
	report, identity := s.Report(), s.Identity()
	stages := campaignStages()
	for _, st := range stages {
		if !na.Contains(s.Names(), st.fixture) {
			return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture CleanlinessFixture,UpkeepFixture,ForecastFixture,RoutineSleepingFixture", st.fixture)
		}
	}
	h := s.Harness()
	var closed []closedGoal
	var timeline []map[string]any
	for i, st := range stages {
		started := time.Now()
		stageReport := na.Report{}
		report["stage_"+st.name] = stageReport
		families := cumulativeFamilies(stages, i+1)
		stageReport["families"] = families
		if _, err := h.Call(ctx, st.name+"-pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
			return fmt.Errorf("stage %s: %w", st.name, err)
		}
		prepared, err := st.prepare(ctx, h, identity, stageReport)
		if err != nil {
			return fmt.Errorf("stage %s: %w", st.name, err)
		}
		stageReport["prepared"] = prepared
		before, err := readUpkeep(ctx, h, identity, st.name+"-upkeep-before")
		if err != nil {
			return fmt.Errorf("stage %s: %w", st.name, err)
		}
		stageReport["upkeep_before"] = before

		spec := s.Spec()
		spec.Families = families
		service, err := s.Serve(ctx, spec)
		if err != nil {
			return fmt.Errorf("stage %s: %w", st.name, err)
		}
		recovered, stageErr := runStage(ctx, s, service, st, prepared, stageReport, closed)
		stageReport["authority_reacquisitions"] = service.Stop()
		// The postmortem read still runs when the budget cancelled ctx.
		afterCtx := ctx
		if ctx.Err() != nil {
			var cancel context.CancelFunc
			afterCtx, cancel = context.WithTimeout(context.Background(), 90*time.Second)
			defer cancel()
		}
		if h, err = reattachPaused(afterCtx, s); err != nil {
			return fmt.Errorf("stage %s: %w", st.name, errors.Join(stageErr, err))
		}
		if stageErr != nil {
			if upkeep, readErr := readUpkeep(afterCtx, h, identity, st.name+"-upkeep-postmortem"); readErr == nil {
				stageReport["upkeep_postmortem"] = upkeep
			} else {
				stageReport["upkeep_postmortem_error"] = readErr.Error()
			}
			return fmt.Errorf("stage %s: %w", st.name, stageErr)
		}
		after, err := readUpkeep(ctx, h, identity, st.name+"-upkeep-after")
		if err != nil {
			return fmt.Errorf("stage %s: %w", st.name, err)
		}
		stageReport["upkeep_after"] = after
		if err := st.verify(ctx, h, identity, prepared, stageReport); err != nil {
			return fmt.Errorf("stage %s: %w", st.name, err)
		}
		closed = append(closed, recovered...)
		row := map[string]any{"stage": st.name, "tick_before": before.Tick, "tick_after": after.Tick, "wall_ms": time.Since(started).Milliseconds()}
		for _, c := range recovered {
			row[string(c.need)] = map[string]any{"goal": string(c.goal), "epoch": c.epoch}
		}
		timeline = append(timeline, row)
		report["timeline"] = timeline
	}
	report["closed_goals"] = len(closed)
	return nil
}

// reattachPaused takes the slot back after a service stop and pauses the
// clock before the stage's native reads and the next stage's fixture (which
// requires a paused map).
func reattachPaused(ctx context.Context, s cases.Session) (*na.Harness, error) {
	h, err := s.Reattach(ctx)
	if err != nil {
		return nil, err
	}
	if _, err := h.Call(ctx, "pause-after", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return nil, err
	}
	return h, nil
}

// runStage drives one stage's service from authority to the stage's
// recovery while guarding every earlier stage's goal, and returns the goals
// this stage recovered.
func runStage(ctx context.Context, s cases.Session, service *na.ServiceProcess, st stage, prepared map[string]any, report na.Report, closed []closedGoal) ([]closedGoal, error) {
	rootPlanID, err := service.Acquire()
	if err != nil {
		return nil, err
	}
	report["root_plan"] = rootPlanID
	service.KeepAuthority(ctx)
	journal, err := service.Store(ctx)
	if err != nil {
		return nil, err
	}
	review, diagnostics, err := service.WaitRoutineReview(ctx, journal, 90*time.Second)
	report["diagnostic_post_acquire"] = diagnostics
	if err != nil {
		return nil, err
	}
	reviewData, _ := json.Marshal(review)
	report["routine_review_first"] = json.RawMessage(reviewData)
	for _, row := range review.Development.Rows {
		if row.Reason == policy.DevelopmentEmergency {
			return nil, fmt.Errorf("first review holds development as an emergency (patients %v); the world is unusable", review.MedicalCare.Patients)
		}
	}
	// The earlier goals are checked on every review while this stage's
	// watch runs, and once more after it: a reopened goal has a tick budget
	// to recover in, and the stage ends only once every one is recovered.
	tracker := newClosedTracker(closed)
	guard := newClosedGuard(ctx, journal, tracker)
	watchErr := st.watch(guard.ctx, journal, prepared, report)
	guardErr := guard.stop()
	report["reopened"] = tracker.reopens
	if guardErr != nil {
		return nil, guardErr
	}
	if watchErr != nil {
		if final, loadErr := journal.LoadRoutineReview(ctx); loadErr == nil {
			data, _ := json.Marshal(final)
			report["routine_review_at_failure"] = json.RawMessage(data)
		}
		return nil, watchErr
	}
	if err := tracker.waitRecovered(ctx, journal); err != nil {
		report["reopened"] = tracker.reopens
		return nil, err
	}
	report["reopened"] = tracker.reopens
	if err := na.AssertRoutineRunning(service.Get); err != nil {
		return nil, err
	}
	var recovered []closedGoal
	for _, need := range st.needs {
		goal, err := waitNeed(ctx, journal, need, domain.NeedRecovered)
		if err != nil {
			return nil, err
		}
		recovered = append(recovered, closedGoal{stage: st.name, need: need, goal: goal.Goal.ID, epoch: goal.Goal.Epoch})
	}
	return recovered, nil
}

// reopenTicks bounds how long an earlier stage's goal may sit in deficit
// again before the campaign fails: three times the cleanliness grace, so a
// room colonists track dirt into is a new deficit the service must close,
// not a regression, while a goal that never recovers is.
const reopenTicks = 90000

// recoveredWait bounds the wall time a stage waits after its own watch for
// every earlier goal to be recovered again.
const recoveredWait = 4 * time.Minute

// closedTracker follows the goals earlier stages recovered. A goal that
// enters a new method epoch is recorded as a reopen and its recorded epoch
// moves on; the failure is a goal that stays in deficit past reopenTicks,
// is rebound to another goal, leaves the journal or is cancelled or
// invalidated.
type closedTracker struct {
	closed       []closedGoal
	deficitSince map[domain.GoalID]domain.Tick
	reopens      []map[string]any
}

func newClosedTracker(closed []closedGoal) *closedTracker {
	return &closedTracker{closed: closed, deficitSince: map[domain.GoalID]domain.Tick{}, reopens: []map[string]any{}}
}

// check reads the journal once and reports which closed goals are in
// deficit, or fails on a violation.
func (t *closedTracker) check(ctx context.Context, journal *store.Store) (inDeficit []closedGoal, err error) {
	if len(t.closed) == 0 {
		return nil, nil
	}
	review, err := journal.LoadRoutineReview(ctx)
	if err != nil {
		if ctx.Err() != nil {
			return nil, nil
		}
		return nil, err
	}
	bound := map[policy.GoalID]domain.GoalID{}
	for _, binding := range review.Goals {
		bound[binding.Need] = binding.Goal
	}
	for i := range t.closed {
		c := &t.closed[i]
		if id, ok := bound[c.need]; ok && id != c.goal {
			return nil, fmt.Errorf("%s (recovered in stage %s as %s) is bound to a new goal %s in review %d", c.need, c.stage, c.goal, id, review.Revision)
		}
		goal, err := journal.LoadGoal(ctx, c.goal)
		if err != nil {
			if ctx.Err() != nil {
				return nil, nil
			}
			if errors.Is(err, store.ErrNotFound) {
				return nil, fmt.Errorf("%s (recovered in stage %s as %s) is gone from the journal", c.need, c.stage, c.goal)
			}
			return nil, err
		}
		if goal.Goal.Status == domain.GoalCancelled || goal.Goal.Status == domain.GoalInvalidated {
			return nil, fmt.Errorf("%s (recovered in stage %s) is %s at review %d", c.need, c.stage, goal.Goal.Status, review.Revision)
		}
		if goal.Goal.Epoch != c.epoch {
			t.reopens = append(t.reopens, map[string]any{
				"need": string(c.need), "stage": c.stage, "goal": string(c.goal),
				"epoch_from": c.epoch, "epoch_to": goal.Goal.Epoch, "tick": review.Tick, "review": review.Revision,
			})
			c.epoch = goal.Goal.Epoch
		}
		if goal.Goal.Need != domain.NeedDeficit {
			delete(t.deficitSince, c.goal)
			continue
		}
		since, seen := t.deficitSince[c.goal]
		if !seen {
			since = review.Tick
			t.deficitSince[c.goal] = since
		}
		if review.Tick-since > reopenTicks {
			return nil, fmt.Errorf("%s (recovered in stage %s) reopened at tick %d and is still in deficit at review %d tick %d, past %d ticks", c.need, c.stage, since, review.Revision, review.Tick, reopenTicks)
		}
		inDeficit = append(inDeficit, *c)
	}
	return inDeficit, nil
}

// waitRecovered polls until no closed goal is in deficit, or fails on the
// tracker's violations or the wall bound.
func (t *closedTracker) waitRecovered(ctx context.Context, journal *store.Store) error {
	deadline := time.Now().Add(recoveredWait)
	for {
		inDeficit, err := t.check(ctx, journal)
		if err != nil {
			return err
		}
		if len(inDeficit) == 0 {
			return nil
		}
		if time.Now().After(deadline) {
			c := inDeficit[0]
			return fmt.Errorf("%s (recovered in stage %s) reopened at tick %d and is still in deficit after %s", c.need, c.stage, t.deficitSince[c.goal], recoveredWait)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(5 * time.Second):
		}
	}
}

// closedGuard polls the tracker while a stage's watch runs and cancels the
// watch the moment it sees a violation.
type closedGuard struct {
	ctx    context.Context
	cancel context.CancelFunc
	done   chan struct{}
	mu     sync.Mutex
	err    error
}

func newClosedGuard(parent context.Context, journal *store.Store, tracker *closedTracker) *closedGuard {
	ctx, cancel := context.WithCancel(parent)
	g := &closedGuard{ctx: ctx, cancel: cancel, done: make(chan struct{})}
	go func() {
		defer close(g.done)
		ticker := time.NewTicker(5 * time.Second)
		defer ticker.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-ticker.C:
			}
			if _, err := tracker.check(ctx, journal); err != nil && ctx.Err() == nil {
				g.mu.Lock()
				g.err = err
				g.mu.Unlock()
				cancel()
				return
			}
		}
	}()
	return g
}

// stop ends the guard and returns the violation it saw, if any.
func (g *closedGuard) stop() error {
	g.cancel()
	<-g.done
	g.mu.Lock()
	defer g.mu.Unlock()
	return g.err
}

// ---- kitchen -------------------------------------------------------------

func prepareKitchen(ctx context.Context, h *na.Harness, identity map[string]any, report na.Report) (map[string]any, error) {
	prepared, err := callFixture(ctx, h, identity, "test/cleanliness_prepare", map[string]any{"scenario": "filthy", "filthPerRoom": 3})
	if err != nil {
		return nil, err
	}
	report["kitchen_filth"] = len(na.AsSlice(prepared["kitchenFilth"]))
	return prepared, nil
}

// kitchenRect reports whether c lies inside the fixture kitchen's wall
// rectangle.
func kitchenRect(prepared map[string]any) func(domain.Cell) bool {
	rect, _ := na.AsMap(prepared["kitchen"])
	return func(c domain.Cell) bool {
		return float64(c.X) >= na.AsNumber(rect["minX"]) && float64(c.X) <= na.AsNumber(rect["maxX"]) &&
			float64(c.Z) >= na.AsNumber(rect["minZ"]) && float64(c.Z) <= na.AsNumber(rect["maxZ"])
	}
}

func watchKitchen(ctx context.Context, journal *store.Store, prepared map[string]any, report na.Report) error {
	deficitCtx, deficitCancel := context.WithTimeout(ctx, 4*time.Minute)
	defer deficitCancel()
	if _, err := waitNeed(deficitCtx, journal, policy.MaintainCleanFacilities, domain.NeedDeficit); err != nil {
		return err
	}
	kitchenFilth := map[string]bool{}
	for _, raw := range na.AsSlice(prepared["kitchenFilth"]) {
		kitchenFilth[na.AsString(raw)] = true
	}
	inKitchen := kitchenRect(prepared)
	// One forced order per filth (the fixture's blood, or dirt the cleaner
	// tracked in), every one inside the kitchen, until the measured room
	// cleanliness recovers the goal.
	seen := map[domain.PlanID]bool{}
	orders := 0
	for {
		if orders > 12 {
			return fmt.Errorf("MaintainCleanFacilities not recovered after %d clean orders", orders)
		}
		label := fmt.Sprintf("clean_%d", orders)
		if _, err := followMethodsExcluding(ctx, journal, policy.MaintainCleanFacilities, label, seen, func(a domain.Action) error {
			clean, ok := a.Clean()
			if !ok {
				return fmt.Errorf("not a clean order: %v", a.Kind())
			}
			if !kitchenFilth[clean.Filth()] && !inKitchen(clean.Cell()) {
				return fmt.Errorf("clean order targets %s at %v, outside the kitchen", clean.Filth(), clean.Cell())
			}
			return nil
		}, report); err != nil {
			return err
		}
		orders++
		report["clean_orders"] = orders
		if report[label+"_recovered_by"] == "ordinary_work" {
			break
		}
		recoverCtx, recoverCancel := context.WithTimeout(ctx, 90*time.Second)
		goal, err := waitNeed(recoverCtx, journal, policy.MaintainCleanFacilities, domain.NeedRecovered)
		recoverCancel()
		if err == nil {
			report["kitchen_recovered_tick"] = int64(goal.Goal.Tick)
			break
		}
		if ctx.Err() != nil {
			return ctx.Err()
		}
	}
	review, err := journal.LoadRoutineReview(ctx)
	if err != nil {
		return err
	}
	report["dirty_rooms_after"] = review.Latches.Upkeep.DirtyRooms
	return nil
}

func verifyKitchen(ctx context.Context, h *na.Harness, identity, prepared map[string]any, report na.Report) error {
	observed, err := readColonyFacts(ctx, h, identity, "kitchen-filth-after")
	if err != nil {
		return err
	}
	section, _ := na.AsMap(observed["upkeep"])
	_, upkeep, err := na.Outcome(section, "observed")
	if err != nil {
		return err
	}
	present := map[string]bool{}
	for _, raw := range na.AsSlice(upkeep["filth"]) {
		row, _ := na.AsMap(raw)
		filth, _ := na.AsMap(row["filth"])
		present[na.AsString(filth["id"])] = true
	}
	for _, raw := range na.AsSlice(prepared["kitchenFilth"]) {
		if id := na.AsString(raw); present[id] {
			return fmt.Errorf("kitchen filth %s survived natively", id)
		}
	}
	butchery := 0
	for _, raw := range na.AsSlice(prepared["butcheryFilth"]) {
		if present[na.AsString(raw)] {
			butchery++
		}
	}
	report["butchery_filth_after"] = butchery
	if butchery == 0 {
		return errors.New("every butchery filth was cleaned; an inherently dirty room is never a target")
	}
	return nil
}
