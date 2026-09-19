// Package condition holds the map-condition response case (issue #408): a
// live game and a live rimgovernor service composed with the lighting,
// refrigeration and mood families on the tribal baseline while a
// solar flare, an eclipse and a psychic drone are all active at once.
//
//	response -- the fixture registers the three conditions, builds an
//	            enclosed roofed room of warm raw meat, stands a fuelled
//	            stove outdoors and a campfire out of its glow, and pins every
//	            colonist's mood a tenth above their own break threshold with
//	            joy at .4. The service must then: open an EnsureMood state
//	            for every pawn of the drone's gender and none of the other
//	            gender (the widened entry margin); admit a cook-ahead
//	            CookMealSimple bill on a fuelled wood bench from
//	            MaintainRefrigeration while the flare keeps the goal without
//	            a cooler; and light the unroofed stove's interaction cell
//	            with a torch under the eclipse, released by the measured
//	            census. The fixture then ends the conditions; the controller
//	            restarted on the same journal must let the mood states go and
//	            keep the stove unlatched (recovery), and an independent native
//	            read confirms the torch lit and the bill standing.
//
// Uses the private disposable test/condition_prepare, test/condition_end
// and test/condition_inspect fixture ops (ConditionFixture.cs). The case's
// own bridge session and the service's are used sequentially, never
// concurrently.
package condition

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

const prefix = "condition-accept"

// conditions are the fixture's three registered defNames.
var conditions = []string{policy.ConditionSolarFlare, policy.ConditionEclipse, policy.ConditionPsychicDrone}

func init() {
	cases.Register(cases.Case{
		Name: "condition/response",
		Scope: "Map-condition responses (#408) on the tribal baseline under a solar flare, an eclipse and a psychic drone at once: " +
			"EnsureMood opens early for the drone's gender alone, MaintainRefrigeration admits a cook-ahead meal bill on a fuelled wood bench " +
			"while the flare holds the coolers, MaintainLighting torches the unroofed stove the eclipse darkened; " +
			"ending the conditions recovers the mood states on a restarted controller, confirmed by an independent native read.",
		// The flare outlives the run at any clock speed: cook-ahead needs it
		// still counting down when the bill planner first steps.
		Start: cases.Fixture{Op: "test/condition_prepare", Args: map[string]any{"flareTicks": 3 * na.TicksPerDay, "eclipseTicks": 3 * na.TicksPerDay, "droneTicks": 3 * na.TicksPerDay}, On: cases.Save{Name: sustained.BaselineSave}},
		// Mood and Joy stay live: the pinned moods are the entry evidence and
		// the freeze would lift both to full.
		Keep:    []string{"Mood", string(na.NeedJoy)},
		Service: true,
		Budget:  15 * time.Minute,
		Run:     run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h, identity, prepared := s.Harness(), s.Identity(), s.Prepared()
	if _, err := na.ConfirmColonyNames(ctx, h, report); err != nil {
		return err
	}
	stoveID, campfireID := na.AsString(prepared["stove"]), na.AsString(prepared["campfire"])
	workCellMap, _ := na.AsMap(prepared["workCell"])
	workCell := domain.Cell{X: int32(na.AsNumber(workCellMap["x"])), Z: int32(na.AsNumber(workCellMap["z"]))}
	affected, unaffected := ids(prepared["affected"]), ids(prepared["unaffected"])
	if stoveID == "" || campfireID == "" || len(affected) == 0 {
		return fmt.Errorf("fixture reply lacks the stove, campfire or affected pawns: %#v", prepared)
	}
	report["drone_gender"] = prepared["droneGender"]
	report["affected"], report["unaffected"] = affected, unaffected

	// Before: the environment census carries all three conditions with a
	// remaining duration, the lighting census lists the stove unroofed and
	// dark, and every affected pawn bears the drone thought while no
	// unaffected pawn does.
	facts, err := readFacts(ctx, h, identity, "facts-before")
	if err != nil {
		return err
	}
	report["environment_before"] = facts.environment
	for _, name := range conditions {
		if ticks, ok := facts.ticksLeft[name]; !ok || ticks <= 0 {
			return fmt.Errorf("facts-before: environment census lacks a timed %s: %#v", name, facts.environment)
		}
	}
	lighting := policy.DefaultLightingPolicy()
	stove, ok := facts.cells[stoveID]
	if !ok || stove.roofed || stove.glow >= lighting.LitGlow {
		return fmt.Errorf("facts-before: fixture stove is not an unroofed dark work cell: %+v", stove)
	}
	report["stove_before"] = map[string]any{"x": stove.cell.X, "z": stove.cell.Z, "glow": stove.glow, "roofed": stove.roofed}
	thoughts, err := droneThoughts(ctx, h, identity, "thoughts-before", append(append([]string{}, affected...), unaffected...))
	if err != nil {
		return err
	}
	report["drone_thoughts_before"] = thoughts
	for _, id := range affected {
		if thoughts[id] >= 0 {
			return fmt.Errorf("thoughts-before: affected pawn %s bears no negative %s thought: %v", id, policy.PsychicDroneThought, thoughts)
		}
	}
	for _, id := range unaffected {
		if thoughts[id] < 0 {
			return fmt.Errorf("thoughts-before: unaffected pawn %s bears the %s thought: %v", id, policy.PsychicDroneThought, thoughts)
		}
	}

	// "work" rides along because every building method's builder check
	// requires the colony's work priorities to match the controller's own
	// assignment. The cooking bill families stay off: the cook-ahead bill
	// nets its target against every standing meal bill's reserve, so an
	// ordinary cooking or preservation bill that landed first would absorb
	// the at-risk stock and leave nothing for it to answer.
	service, err := s.Launch(ctx, na.ServiceLaunch{
		Families: []string{"lighting", "work", "refrigeration", "mood"},
		Extra:    na.ClockSpeedArgs(),
	})
	if err != nil {
		return err
	}
	defer service.Stop()
	token, err := service.SessionToken()
	if err != nil {
		return err
	}
	attached, err := service.WaitAttached(identity, 90*time.Second)
	if err != nil {
		return err
	}
	report["service_state_attached"] = attached
	rootPlanID, err := service.Resume(prefix, identity, token, report)
	if err != nil {
		return err
	}
	report["root_plan"] = rootPlanID
	keepAlive := &na.AuthorityKeepAlive{Service: service, Prefix: prefix, Identity: identity, Token: token}
	stopKeepAlive := keepAlive.Start(ctx)
	stopped := map[string]any{}
	defer func() {
		if stopKeepAlive != nil {
			stopped["running"] = stopKeepAlive()
		}
		report["authority_reacquisitions"] = stopped
	}()
	journal, err := na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		return err
	}
	defer journal.Close()
	review, diagnostics, err := service.WaitRoutineReview(ctx, journal, 90*time.Second)
	report["diagnostic_post_acquire"] = diagnostics
	if err != nil {
		return err
	}
	reviewData, _ := json.Marshal(review)
	report["routine_review_first"] = json.RawMessage(reviewData)

	// Psychic drone: the review opens a mood state for every affected pawn
	// (mood a tenth above threshold, within the drone's margin) and for no
	// unaffected pawn (same mood, ordinary margin) short of a mental risk.
	moodCtx, moodCancel := context.WithTimeout(ctx, 3*time.Minute)
	entered, err := na.WaitReview(moodCtx, journal, storeWait(service), func(r store.RoutineReview) bool {
		return moodStates(r, affected, unaffected) == nil
	})
	moodCancel()
	if err != nil {
		return fmt.Errorf("mood entry under the drone: %w (revision %d: %v)", err, entered.Revision, moodStates(entered, affected, unaffected))
	}
	report["mood_entered_review_revision"] = entered.Revision
	report["mood_states_entered"] = moodEvidence(entered, append(append([]string{}, affected...), unaffected...))

	// Solar flare: MaintainRefrigeration keeps a method while the coolers
	// are dark -- a cook-ahead CookMealSimple bill on one of the fixture's
	// fuelled wood benches, sized to the warm at-risk stock.
	billCtx, billCancel := context.WithTimeout(ctx, 5*time.Minute)
	bill, err := waitCookAhead(billCtx, journal, map[string]bool{stoveID: true, campfireID: true})
	billCancel()
	if err != nil {
		return fmt.Errorf("cook-ahead bill under the flare: %w", err)
	}
	report["cook_ahead"] = map[string]any{"plan": string(bill.plan), "bench": bill.bench, "recipe": bill.recipe, "target": bill.target}
	// The bill stands once its write is receipted; the plan's own completion
	// waits for the first iteration's meals to be observed in place, which
	// eight hauling colonists can defeat by merging the stack first. The
	// native read after the service stops confirms the bill itself.
	billDoneCtx, billDoneCancel := context.WithTimeout(ctx, 5*time.Minute)
	billTick, err := waitBillReceipted(billDoneCtx, journal, bill.plan)
	billDoneCancel()
	if err != nil {
		return fmt.Errorf("cook-ahead plan: %w", err)
	}
	report["cook_ahead_receipted_tick"] = int64(billTick)

	// Eclipse: the unroofed stove is latched and torched within the
	// placement radius, and the measured census releases the latch.
	torch, err := admitTorch(ctx, journal, service, stoveID, workCell, lighting.PlacementRadius, report)
	if err != nil {
		return err
	}
	if err := na.AssertRoutineRunning(service.Get); err != nil {
		return err
	}

	// Independent native read once the service lets go of the game: the
	// stove cell lit by the admitted torch, the bill standing on its bench
	// and the three conditions still counting down.
	journal.Close()
	stopped["first"], stopKeepAlive = stopKeepAlive(), nil
	service.Stop()
	if h, err = s.Reattach(ctx); err != nil {
		return fmt.Errorf("reopen harness session after service stop: %w", err)
	}
	if _, err := h.Call(ctx, "pause-after", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	inspected, err := inspect(ctx, h, "inspect-after", stoveID, campfireID)
	if err != nil {
		return err
	}
	report["inspect_after"] = inspected
	if glow := na.AsNumber(inspected["glow"]); glow < lighting.LitGlow {
		return fmt.Errorf("inspect-after: stove cell reads %.2f, under %.2f, after the torch at %v", glow, lighting.LitGlow, torch)
	}
	if !billStands(inspected, bill, stoveID) {
		return fmt.Errorf("inspect-after: no %s bill stands on %s: stove %v campfire %v", bill.recipe, bill.bench, inspected["stoveBills"], inspected["campfireBills"])
	}
	for _, name := range conditions {
		if !activeCondition(inspected, name) {
			return fmt.Errorf("inspect-after: %s ended before the recovery half: %v", name, inspected["active"])
		}
	}

	// Recovery: the fixture ends the conditions and re-pins the moods (joy
	// high); the drone thought must leave every affected pawn on ordinary
	// ticks before the controller returns.
	ended, err := h.Call(ctx, "condition-end", "test/condition_end", nil)
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(ended["success"]); !success {
		return fmt.Errorf("condition-end: condition_end refused: %#v", ended)
	}
	report["condition_end"] = ended
	for _, name := range conditions {
		if activeCondition(ended, name) {
			return fmt.Errorf("condition-end: %s still active: %v", name, ended["active"])
		}
	}
	var cleared map[string]float64
	elapsed, err := na.RunUntil(ctx, h, "thoughts-clear", 4*na.TicksPerHour, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		cleared, err = droneThoughts(ctx, h, identity, "thoughts-clear-probe", affected)
		if err != nil {
			return "", false, err
		}
		remaining := 0
		for _, offset := range cleared {
			if offset < 0 {
				remaining++
			}
		}
		return na.Signature(remaining), remaining == 0, nil
	})
	report["thoughts_clear_ticks"] = elapsed
	report["drone_thoughts_after"] = cleared
	if err != nil {
		return fmt.Errorf("thoughts-clear: %w", err)
	}
	if _, err := h.Call(ctx, "pause-recovery", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}

	// The controller returns on the same journal: its released review must
	// let every mood state go and keep the lit stove unlatched.
	if err := s.Release(); err != nil {
		return err
	}
	service.Identity = identity
	restarted, err := service.Restart(ctx)
	if err != nil {
		return fmt.Errorf("restart the controller after the conditions ended: %w", err)
	}
	service = restarted
	defer service.Stop()
	const recoveryPrefix = prefix + "-recovery"
	token = service.Token
	if _, err := service.Resume(recoveryPrefix, identity, token, report); err != nil {
		return fmt.Errorf("resume after the conditions ended: %w", err)
	}
	keepAlive = &na.AuthorityKeepAlive{Service: service, Prefix: recoveryPrefix, Identity: identity, Token: token}
	stopKeepAlive = keepAlive.Start(ctx)
	journal, err = na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		return err
	}
	defer journal.Close()
	recoveryCtx, recoveryCancel := context.WithTimeout(ctx, 4*time.Minute)
	recovered, err := na.WaitReview(recoveryCtx, journal, storeWait(service), func(r store.RoutineReview) bool {
		return r.Revision > entered.Revision && moodStates(r, nil, append(append([]string{}, affected...), unaffected...)) == nil && !latchedOn(r, stoveID)
	})
	recoveryCancel()
	if err != nil {
		return fmt.Errorf("recovery review: %w (revision %d: mood %v, lighting latches %v)", err, recovered.Revision, moodStates(recovered, nil, affected), recovered.Latches.Lighting)
	}
	report["recovered_review_revision"] = recovered.Revision
	report["mood_states_recovered"] = moodEvidence(recovered, append(append([]string{}, affected...), unaffected...))
	if err := na.AssertRoutineRunning(service.Get); err != nil {
		return err
	}
	journal.Close()
	stopped["recovery"], stopKeepAlive = stopKeepAlive(), nil
	service.Stop()
	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}

func ids(raw any) []string {
	var out []string
	for _, v := range na.AsSlice(raw) {
		if id := na.AsString(v); id != "" {
			out = append(out, id)
		}
	}
	return out
}

func storeWait(service *na.ServiceProcess) na.Wait {
	return na.Wait{Stall: na.StallBudget(), Terminal: service.Exited}
}

// moodStates is nil when every pawn in active has an active mood state and
// no pawn in inactive does (a mental risk excuses the latter); otherwise it
// names the first pawn that is wrong.
func moodStates(r store.RoutineReview, active, inactive []string) error {
	states := map[string]store.RoutineMoodState{}
	if r.Mood != nil {
		for _, s := range r.Mood.States {
			states[string(s.Pawn.ID)] = s
		}
	}
	for _, id := range active {
		if s, ok := states[id]; !ok || !s.Active {
			return fmt.Errorf("affected pawn %s has no active mood state", id)
		}
	}
	for _, id := range inactive {
		if s, ok := states[id]; ok && s.Active && !s.MentalRisk {
			return fmt.Errorf("pawn %s has an active mood state", id)
		}
	}
	return nil
}

func moodEvidence(r store.RoutineReview, pawns []string) map[string]any {
	out := map[string]any{}
	if r.Mood == nil {
		return out
	}
	for _, s := range r.Mood.States {
		for _, id := range pawns {
			if string(s.Pawn.ID) != id {
				continue
			}
			row := map[string]any{"active": s.Active, "mental_risk": s.MentalRisk, "causes": len(s.Causes)}
			if s.Pawn.Mood != nil {
				row["mood"] = *s.Pawn.Mood
			}
			if s.Pawn.Threshold != nil {
				row["threshold"] = *s.Pawn.Threshold
			}
			out[id] = row
		}
	}
	return out
}

// cookAhead is the bill method waitCookAhead found on MaintainRefrigeration.
type cookAhead struct {
	plan          domain.PlanID
	bench, recipe string
	target        int32
}

// waitCookAhead follows MaintainRefrigeration methods until one is a
// production bill: the cooler methods the family may try first are skipped,
// and the bill must be the simple-meal recipe on a fixture bench.
func waitCookAhead(ctx context.Context, journal *store.Store, benches map[string]bool) (cookAhead, error) {
	seen := map[domain.PlanID]bool{}
	for {
		_, method, err := na.WaitGoalMethodExcluding(ctx, journal, policy.MaintainRefrigeration, seen)
		if err != nil {
			return cookAhead{}, err
		}
		seen[method.Plan] = true
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return cookAhead{}, err
		}
		actions := plan.Spec.Actions()
		if len(actions) != 1 {
			continue
		}
		b, ok := actions[0].ProductionBill()
		if !ok {
			continue
		}
		if b.Recipe() != "CookMealSimple" || !benches[b.Bench()] {
			return cookAhead{}, fmt.Errorf("MaintainRefrigeration admitted bill %s on %s; expected CookMealSimple on a fixture wood bench", b.Recipe(), b.Bench())
		}
		return cookAhead{plan: method.Plan, bench: b.Bench(), recipe: b.Recipe(), target: b.Target()}, nil
	}
}

// admitTorch follows MaintainLighting methods until one places a lamp
// within the placement radius of the stove's work cell (other unroofed
// benches of the baseline may be torched first under the eclipse), waits
// for the build and for the measured census to release the stove's latch.
func admitTorch(ctx context.Context, journal *store.Store, service *na.ServiceProcess, stoveID string, work domain.Cell, radius int32, report na.Report) (domain.Cell, error) {
	// The torch plan is often admitted and finished before the case reaches
	// this point (the lighting review runs alongside the mood entry and the
	// cook-ahead bill), so the latch itself is not awaited: a MaintainLighting
	// method exists only while the goal is in deficit, and the release below
	// proves the latch cleared once the torch stood.
	seen := map[domain.PlanID]bool{}
	var built domain.Cell
	others := 0
	for {
		methodCtx, methodCancel := context.WithTimeout(ctx, 8*time.Minute)
		_, method, err := na.WaitGoalMethodExcluding(methodCtx, journal, policy.MaintainLighting, seen)
		methodCancel()
		if err != nil {
			return domain.Cell{}, fmt.Errorf("lighting method for the stove: %w", err)
		}
		seen[method.Plan] = true
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return domain.Cell{}, err
		}
		actions := plan.Spec.Actions()
		if len(actions) != 1 {
			return domain.Cell{}, fmt.Errorf("lighting plan %s has %d actions, expected 1", method.Plan, len(actions))
		}
		b, ok := actions[0].Building()
		if !ok {
			return domain.Cell{}, fmt.Errorf("lighting plan action is not a building: %#v", actions[0])
		}
		cell := b.Cell()
		if b.Definition() != "TorchLamp" {
			return domain.Cell{}, fmt.Errorf("lighting admitted %s; a colony without a power source must choose the TorchLamp", b.Definition())
		}
		if cell == work || max(abs(cell.X-work.X), abs(cell.Z-work.Z)) > radius {
			// Another bench's torch: let it finish so the stove's turn comes.
			others++
			doneCtx, doneCancel := context.WithTimeout(ctx, 5*time.Minute)
			_, _, err := na.WaitPlanTerminal(doneCtx, journal, method.Plan)
			doneCancel()
			if err != nil {
				return domain.Cell{}, fmt.Errorf("another bench's lighting plan: %w", err)
			}
			continue
		}
		doneCtx, doneCancel := context.WithTimeout(ctx, 8*time.Minute)
		state, incidental, err := na.WaitPlanTerminal(doneCtx, journal, method.Plan)
		doneCancel()
		if err != nil {
			return domain.Cell{}, fmt.Errorf("lighting plan: %w", err)
		}
		if incidental {
			continue
		}
		built = cell
		report["torch"] = map[string]any{"plan": string(method.Plan), "x": cell.X, "z": cell.Z, "completed_tick": int64(state.Progress[0].View().Tick), "other_benches_first": others}
		break
	}
	releaseCtx, releaseCancel := context.WithTimeout(ctx, 5*time.Minute)
	released, err := na.WaitReview(releaseCtx, journal, storeWait(service), func(r store.RoutineReview) bool { return !latchedOn(r, stoveID) })
	releaseCancel()
	if err != nil {
		return domain.Cell{}, fmt.Errorf("lighting release on the stove: %w (revision %d)", err, released.Revision)
	}
	report["lighting_released_review_revision"] = released.Revision
	return built, nil
}

// waitBillReceipted waits until the plan's single bill action carries an
// accepted receipt (dispatched, or already completed), returning the
// dispatch tick. Unsuccessful or cancelled stages fail.
func waitBillReceipted(ctx context.Context, journal *store.Store, planID domain.PlanID) (domain.Tick, error) {
	var tick domain.Tick
	err := na.WaitProgress(ctx, na.Wait{Stall: na.StallBudget(), Interval: time.Second}, func(ctx context.Context) (string, bool, error) {
		state, err := journal.LoadPlan(ctx, planID)
		if err != nil {
			return "", false, err
		}
		if len(state.Progress) != 1 {
			return "", false, fmt.Errorf("plan %s has %d actions, expected 1", planID, len(state.Progress))
		}
		view := state.Progress[0].View()
		switch view.Stage {
		case domain.Unsuccessful, domain.Cancelled:
			return "", false, fmt.Errorf("plan %s reached %s before its bill was written", planID, view.Stage)
		}
		receipt, known := view.Receipt.Value()
		tick = view.Tick
		return na.Signature(view.Stage, view.Attempt, receipt), known && receipt == domain.ReceiptAccepted, nil
	})
	return tick, err
}

func latchedOn(review store.RoutineReview, bench string) bool {
	for _, id := range review.Latches.Lighting {
		if id == bench {
			return true
		}
	}
	return false
}

func abs(v int32) int32 {
	if v < 0 {
		return -v
	}
	return v
}

type workCellRow struct {
	cell   domain.Cell
	glow   float64
	roofed bool
}

// colonyFacts is the slice of the colony facts read this case reviews: the
// environment census by condition and the lighting census by bench.
type colonyFacts struct {
	environment []map[string]any
	ticksLeft   map[string]float64
	cells       map[string]workCellRow
}

func readFacts(ctx context.Context, h *na.Harness, identity map[string]any, label string) (colonyFacts, error) {
	reply, err := h.Wire(ctx, label, "observations_read_colony_facts", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "planning": false, "page": map[string]any{"limit": 256},
	})
	if err != nil {
		return colonyFacts{}, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return colonyFacts{}, err
	}
	f := colonyFacts{ticksLeft: map[string]float64{}, cells: map[string]workCellRow{}}
	for _, raw := range na.AsSlice(observed["environment"]) {
		row, _ := na.AsMap(raw)
		f.environment = append(f.environment, row)
		if permanent, _ := na.AsBool(row["permanent"]); !permanent {
			f.ticksLeft[na.AsString(row["defName"])] = na.AsNumber(row["ticksLeft"])
		}
	}
	upkeepSection, _ := na.AsMap(observed["upkeep"])
	_, upkeep, err := na.Outcome(upkeepSection, "observed")
	if err != nil {
		return colonyFacts{}, fmt.Errorf("%s: upkeep unavailable: %w", label, err)
	}
	section, _ := na.AsMap(upkeep["lighting"])
	_, lighting, err := na.Outcome(section, "observed")
	if err != nil {
		return colonyFacts{}, fmt.Errorf("%s: lighting section unavailable: %w", label, err)
	}
	for _, raw := range na.AsSlice(lighting["workCells"]) {
		row, _ := na.AsMap(raw)
		bench, _ := na.AsMap(row["bench"])
		cell, _ := na.AsMap(row["cell"])
		roofed, _ := na.AsBool(row["roofed"])
		f.cells[na.AsString(bench["id"])] = workCellRow{cell: domain.Cell{X: int32(na.AsNumber(cell["x"])), Z: int32(na.AsNumber(cell["z"]))}, glow: na.AsNumber(row["glow"]), roofed: roofed}
	}
	return f, nil
}

// droneThoughts reads the pawns with the routine census's social detail
// and reports each one's PsychicDrone thought offset as the mood census
// lifts it (negative rows only; 0 when absent).
func droneThoughts(ctx context.Context, h *na.Harness, identity map[string]any, label string, pawns []string) (map[string]float64, error) {
	reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{
		"scope":   map[string]any{"expectedIdentity": identity},
		"filter":  map[string]any{"ids": pawns, "includeDead": true},
		"details": map[string]any{"needs": true, "social": true},
		"page":    map[string]any{"limit": len(pawns)},
	})
	if err != nil {
		return nil, err
	}
	encoded, err := json.Marshal(reply)
	if err != nil {
		return nil, err
	}
	typed := &o.ListPawnsReply{}
	if err = protojson.Unmarshal(encoded, typed); err != nil {
		return nil, fmt.Errorf("%s: decode pawn reply: %w", label, err)
	}
	observed := typed.GetObserved()
	if observed == nil || len(observed.Pawns) != len(pawns) {
		return nil, fmt.Errorf("%s: expected %d pawns, got %#v", label, len(pawns), reply)
	}
	out := map[string]float64{}
	for _, row := range observed.Pawns {
		thoughts, known := observation.MoodThoughts(row).Value()
		if !known {
			return nil, fmt.Errorf("%s: pawn %s left the thought census unknown", label, row.GetPawn().GetId())
		}
		out[row.GetPawn().GetId()] = 0
		for _, t := range thoughts {
			if t.Def == policy.PsychicDroneThought {
				out[row.GetPawn().GetId()] = t.Offset
			}
		}
	}
	return out, nil
}

func inspect(ctx context.Context, h *na.Harness, label, stove, campfire string) (map[string]any, error) {
	reply, err := h.Call(ctx, label, "test/condition_inspect", map[string]any{"stove": stove, "campfire": campfire})
	if err != nil {
		return nil, err
	}
	if success, _ := na.AsBool(reply["success"]); !success {
		return nil, fmt.Errorf("%s: condition_inspect refused: %#v", label, reply)
	}
	return reply, nil
}

func activeCondition(reply map[string]any, name string) bool {
	for _, raw := range na.AsSlice(reply["active"]) {
		row, _ := na.AsMap(raw)
		if na.AsString(row["defName"]) == name {
			return true
		}
	}
	return false
}

func billStands(inspected map[string]any, bill cookAhead, stoveID string) bool {
	key := "campfireBills"
	if bill.bench == stoveID {
		key = "stoveBills"
	}
	for _, raw := range na.AsSlice(inspected[key]) {
		row, _ := na.AsMap(raw)
		if na.AsString(row["recipe"]) == bill.recipe {
			return true
		}
	}
	return false
}
