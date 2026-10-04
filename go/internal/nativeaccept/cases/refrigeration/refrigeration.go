// Package refrigeration holds the MaintainRefrigeration vertical (issue #6
// slice 1, milestone B) on the lab contract (#747): a live game and a live
// rimgovernor Go player-control service composed with the refrigeration
// family.
//
//	build -- an enclosed roofed stockpile room on the blank lab holds warm
//	         raw meat and has no cooler. The service must admit exactly one
//	         Cooler on a wall cell of that room with its hot side outdoors,
//	         the colonists build it, and native cooling then takes the
//	         measured room temperature under the review's exit threshold.
//
// The case ends with spoilage recovery: the fixture seeds one meat stack
// part-way to rotting, and once the room is chilled the case advances the
// game until that stack measures under 0 C and then over a further window
// in which its CompRottable progress must not move (issue #159).
//
// The setpoint, power and season decisions are snapshot tests over their
// recorded reviews (internal/buildingruntime, #747).
//
// Uses the private disposable test/refrigeration_prepare fixture
// (RefrigerationFixture.cs). The case's own bridge session and the
// service's are used sequentially, never concurrently (one GABP client per
// game).
package refrigeration

import (
	"context"
	"encoding/json"
	"fmt"
	"math"
	"os"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

const prefix = "refrigeration-accept"

func init() {
	cases.Register(cases.Case{
		Name: "refrigeration/build",
		Scope: "Native MaintainRefrigeration vertical on the lab: warm at-risk meat in an enclosed room drives the live Go " +
			"rounder/planner to admit a Cooler on a vented wall; native cooling then takes the measured room under " +
			"the exit threshold, confirmed by an independent native read, and the seeded rotting stack's rot progress " +
			"stops advancing.",
		Start: cases.Fixture{Op: "test/refrigeration_prepare", Args: map[string]any{
			"existingCooler": false, "disconnected": false, "roomTemperatureC": 30, "season": false,
			"coolerTargetC": 21.0, "foodDef": "Meat_Muffalo", "rotStacks": 1, "rotProgressFraction": 0.25,
			"roomWidth": 6, "roomHeight": 4,
		}, On: cases.LabStart()},
		Service: true,
		Budget:  8 * time.Minute,
		Run:     run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h, identity, prepared := s.Harness(), s.Identity(), s.Prepared()
	var service *na.ServiceProcess
	// Failure evidence: the same independent food read a pass ends with,
	// so a dropped refrigeration latch can be explained (stock hauled out,
	// rotted, or genuinely chilled), and the emergency reviewer's own
	// threat census, so an unsafe_colony hold names the pawns behind it.
	defer func() {
		if _, hasAfter := report["food_after"]; hasAfter {
			return
		}
		if service != nil {
			service.Stop()
		}
		stopCtx, stopCancel := context.WithTimeout(context.Background(), 90*time.Second)
		defer stopCancel()
		ph, err := s.Reattach(stopCtx)
		if err != nil {
			report["postmortem_error"] = "reopen session for the postmortem: " + err.Error()
			return
		}
		if after, err := readFoodStorage(stopCtx, ph, identity, "food-postmortem"); err == nil {
			report["food_postmortem"] = after.evidence()
		} else {
			report["food_postmortem_error"] = err.Error()
		}
		if reply, err := ph.Wire(stopCtx, "threats-postmortem", "observations_read_status", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "colonists": false, "threats": true,
		}); err == nil {
			if _, observed, err := na.Outcome(reply, "observed"); err == nil {
				report["threats_postmortem"] = observed["threats"]
			}
		}
	}()
	if _, err := na.ConfirmColonyNames(ctx, h, report); err != nil {
		return err
	}
	interior, _ := na.AsMap(prepared["interior"])
	walls := map[domain.Cell]bool{}
	for _, raw := range na.AsSlice(prepared["walls"]) {
		w, _ := na.AsMap(raw)
		walls[domain.Cell{X: int32(na.AsNumber(w["x"])), Z: int32(na.AsNumber(w["z"]))}] = true
	}
	inside := func(c domain.Cell) bool {
		return float64(c.X) >= na.AsNumber(interior["minX"]) && float64(c.X) <= na.AsNumber(interior["maxX"]) &&
			float64(c.Z) >= na.AsNumber(interior["minZ"]) && float64(c.Z) <= na.AsNumber(interior["maxZ"])
	}

	// Before: the typed colony facts must show the meat warm, roofed, in the
	// fixture room and short of runway -- the exact facts the review latches
	// on.
	before, err := readFoodStorage(ctx, h, identity, "food-before")
	if err != nil {
		return err
	}
	report["food_before"] = before.evidence()
	policyDefaults := policy.DefaultFoodStoragePolicy()
	if before.warmNutrition < policyDefaults.AtRiskNutritionThreshold {
		return fmt.Errorf("food-before: fixture meat is not warm at-risk stock (warm nutrition %.2f, temperature %.1f C, roofed %d/%d)",
			before.warmNutrition, before.temperature, before.roofed, before.rows)
	}

	rotting, _ := na.AsMap(prepared["rotting"])
	rotID := na.AsString(rotting["id"])
	if rotID == "" {
		return fmt.Errorf("fixture spawned no part-rotted meat stack: %#v", prepared["rotting"])
	}
	rotBefore, err := readRot(ctx, h, rotID, "rot-before")
	if err != nil {
		return err
	}
	report["rot_before"] = rotBefore.evidence()
	if rotBefore.destroyed || rotBefore.stage != "Fresh" || rotBefore.progress <= 0 {
		return fmt.Errorf("rot-before: seeded stack %s is not fresh with rot progress (%+v)", rotID, rotBefore)
	}

	// "work" rides along because every building method's builder check
	// (comfortBuilderAvailable) requires the colony's work priorities to match
	// the controller's own assignment, which only the work family applies.
	service, err = s.Launch(ctx, na.ServiceLaunch{Families: []string{"refrigeration", "work"}, Extra: na.ClockSpeedArgs()})
	if err != nil {
		return err
	}
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
	defer func() { report["authority_reacquisitions"] = stopKeepAlive() }()

	journal, err := na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		return err
	}
	defer journal.Close()
	review, diagnostics, err := service.WaitRounds(ctx, journal, 90*time.Second)
	report["diagnostic_post_acquire"] = diagnostics
	if err != nil {
		return err
	}
	reviewData, _ := json.Marshal(review)
	report["routine_review_first"] = json.RawMessage(reviewData)

	// The review must latch refrigeration and bind MaintainRefrigeration.
	waitCtx, waitCancel := context.WithTimeout(ctx, 3*time.Minute)
	latched, err := waitLatch(waitCtx, journal, service)
	waitCancel()
	if err != nil {
		return fmt.Errorf("refrigeration latch: %w", err)
	}
	report["latched_review_revision"] = latched.Revision

	// The refrigeration method: a Cooler build on a wall cell.
	methodCtx, methodCancel := context.WithTimeout(ctx, 8*time.Minute)
	goalID, method, err := na.WaitGoalMethod(methodCtx, journal, policy.MaintainRefrigeration, nil)
	methodCancel()
	if err != nil {
		return fmt.Errorf("refrigeration method: %w", err)
	}
	report["goal_id"] = string(goalID)
	var builtCell domain.Cell
	for renewals := 0; ; renewals++ {
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return err
		}
		actions := plan.Spec.Actions()
		if len(actions) != 1 {
			return fmt.Errorf("refrigeration plan %s has %d actions, expected 1", method.Plan, len(actions))
		}
		b, ok := actions[0].Building()
		if !ok || b.Definition() != "Cooler" {
			return fmt.Errorf("refrigeration plan action is not a Cooler build: %#v", actions[0])
		}
		builtCell = b.Cell()
		if !walls[builtCell] {
			return fmt.Errorf("cooler placed at %v, not on a fixture wall cell", builtCell)
		}
		placed := policy.RefrigerationCooler{Position: builtCell, Rotation: b.Rotation()}
		cold, hot := placed.Cold(), placed.Hot()
		if !inside(cold) || inside(hot) || walls[hot] {
			return fmt.Errorf("cooler at %v facing %s has cold side %v / hot side %v; expected cold inside and hot outdoors", builtCell, b.Rotation(), cold, hot)
		}
		report["cooler_cell"] = map[string]any{"x": builtCell.X, "z": builtCell.Z, "rotation": string(b.Rotation())}
		doneCtx, doneCancel := context.WithTimeout(ctx, 10*time.Minute)
		state, incidental, err := na.WaitPlanTerminal(doneCtx, journal, method.Plan)
		doneCancel()
		if err != nil {
			return fmt.Errorf("refrigeration plan: %w", err)
		}
		if !incidental {
			report["refrigeration_plan"] = string(method.Plan)
			report["refrigeration_completed_tick"] = int64(state.Progress[0].View().Tick)
			report["incidental_renewals"] = renewals
			break
		}
		renewCtx, renewCancel := context.WithTimeout(ctx, 5*time.Minute)
		_, method, err = na.WaitGoalMethod(renewCtx, journal, policy.MaintainRefrigeration, &method)
		renewCancel()
		if err != nil {
			return fmt.Errorf("renewed refrigeration method after incidental cancellation #%d: %w", renewals+1, err)
		}
	}

	// Native cooling: the review releases its latch only when the stock's
	// measured temperature falls to ChilledExitC, and the goal is then
	// retired. Wait for that release from the journal itself.
	coolCtx, coolCancel := context.WithTimeout(ctx, 12*time.Minute)
	released, err := waitRelease(coolCtx, journal, service)
	coolCancel()
	if err != nil {
		return fmt.Errorf("refrigeration release: %w", err)
	}
	report["released_review_revision"] = released.Revision
	methods, err := refrigerationMethods(ctx, journal)
	if err != nil {
		return err
	}
	report["refrigeration_methods"] = len(methods)
	if err := na.AssertRoutineRunning(service.Get); err != nil {
		return err
	}

	// Independent native read after the service releases the game slot.
	journal.Close()
	service.Stop()
	if h, err = s.Reattach(ctx); err != nil {
		return fmt.Errorf("reopen harness session after service stop: %w", err)
	}
	if _, err := h.Call(ctx, "pause-after", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	after, err := readFoodStorage(ctx, h, identity, "food-after")
	if err != nil {
		return err
	}
	report["food_after"] = after.evidence()
	// The controller's release above is the exit-threshold evidence at its
	// own tick. The game runs on for a few hundred ticks while the service
	// hands the slot back, so this later independent read confirms the stock
	// is chilled (under the review's entry bound) with no warm nutrition
	// left, rather than re-applying the hysteresis exit bound.
	if after.rows == 0 || after.temperature > after.chilledMaxC || after.warmNutrition > 0 {
		return fmt.Errorf("food-after: meat temperature %.1f C / warm nutrition %.2f is not chilled under %.1f C (rows %d)", after.temperature, after.warmNutrition, after.chilledMaxC, after.rows)
	}
	coolers, err := readCoolers(ctx, h, identity)
	if err != nil {
		return err
	}
	coolerEvidence := make([]map[string]any, 0, len(coolers))
	for _, c := range coolers {
		coolerEvidence = append(coolerEvidence, c.evidence())
	}
	report["coolers_after"] = coolerEvidence
	if len(coolers) != 1 {
		return fmt.Errorf("expected exactly 1 Cooler after the run, observed %d: %#v", len(coolers), coolers)
	}
	c := coolers[0]
	if c.x != builtCell.X || c.z != builtCell.Z {
		return fmt.Errorf("cooler %s at (%d,%d) is not at the admitted cell %v", c.id, c.x, c.z, builtCell)
	}
	if c.target > policyDefaults.FreezerTargetC {
		return fmt.Errorf("cooler %s target %.1f C is above the freezer target %.1f C", c.id, c.target, policyDefaults.FreezerTargetC)
	}
	if err := checkSpoilageRecovery(ctx, s, rotID, rotBefore); err != nil {
		return fmt.Errorf("spoilage recovery: %w", err)
	}
	return checkStartupLog(s)
}

// Spoilage recovery: rot progress advances (in 250-tick intervals) one rot
// tick per game tick above ChilledMaxC, proportionally between 0 and 10 C
// and not at all under 0 C. freezeWindowTicks is one advance while the cooler pulls the room from the
// release threshold to its freezer target, freezeWindows bounds that wait
// (half a game day), and the measured window is rotWindowSteps advances of
// rotStepTicks (one game hour, sampled at CompRottable's own 250-tick
// cadence). The door still opens for the colonists' own traffic and a
// rare tick can land on the spike that lets in (seen at 5 of 10 steps under
// a heat wave), so the window as a whole is the assertion: at least
// rotStoppedSteps steps move the stack's progress by at most
// rotStoppedTolerance rot ticks (a rare tick within 0.1 C of zero), and the
// whole window advances it by under rotWindowMaxFraction of its ticks
// against the one-per-tick warm rate: a room merely chilled to ChilledExitC
// would read 50%, a warm one 100%.
const (
	freezeWindowTicks    = 2500
	freezeWindows        = 12
	rotStepTicks         = 250
	rotWindowSteps       = 10
	rotStoppedSteps      = 5
	rotStoppedTolerance  = 2.5
	rotWindowMaxFraction = 0.25
)

// checkSpoilageRecovery is the run's spoilage assertion over the seeded
// rotting stack: still fresh after the run (its rot advance over the run is
// recorded, not bounded: a setpoint patch chills in a few hundred ticks, a
// build in a few thousand), and once it measures under 0 C its CompRottable
// progress stops for the bulk of a measured hour.
func checkSpoilageRecovery(ctx context.Context, s cases.Session, rotID string, before rotSummary) error {
	report := s.Report()
	after, err := readRot(ctx, s.Harness(), rotID, "rot-after")
	if err != nil {
		return err
	}
	report["rot_after"] = after.evidence()
	if after.destroyed || after.stage != "Fresh" {
		return fmt.Errorf("seeded stack %s did not survive the run fresh: %+v", rotID, after)
	}
	report["rot_run_advance"] = map[string]any{"rot_ticks": after.progress - before.progress, "game_ticks": after.tick - before.tick}
	windows := 0
	for ; after.temperature >= 0; windows++ {
		if windows == freezeWindows {
			return fmt.Errorf("seeded stack still measures %.1f C after %d ticks with the cooler running: rot never stopped", after.temperature, windows*freezeWindowTicks)
		}
		if _, err := s.Advance(ctx, freezeWindowTicks); err != nil {
			return err
		}
		if after, err = readRot(ctx, s.Harness(), rotID, "rot-freeze"); err != nil {
			return err
		}
		if after.destroyed {
			return fmt.Errorf("seeded stack %s vanished while the room froze: %+v", rotID, after)
		}
	}
	report["rot_freeze_windows"] = windows
	first, start, stopped := after, after, 0
	steps := make([]map[string]any, 0, rotWindowSteps)
	report["rot_window"] = map[string]any{"start": start.evidence(), "steps": &steps}
	for step := 0; step < rotWindowSteps; step++ {
		if _, err := s.Advance(ctx, rotStepTicks); err != nil {
			return err
		}
		end, err := readRot(ctx, s.Harness(), rotID, "rot-window")
		if err != nil {
			return err
		}
		if end.destroyed || end.stage != "Fresh" {
			return fmt.Errorf("seeded stack %s did not survive the frozen window fresh: %+v", rotID, end)
		}
		advanced := end.progress - start.progress
		steps = append(steps, map[string]any{"tick": end.tick, "temperature_c": end.temperature, "rot_advance": advanced, "pawns_in_room": end.pawnsInRoom})
		if advanced <= rotStoppedTolerance {
			stopped++
		}
		start = end
	}
	total, ticks := start.progress-first.progress, start.tick-first.tick
	report["rot_stopped_steps"] = stopped
	report["rot_window_advance"] = map[string]any{"rot_ticks": total, "game_ticks": ticks}
	if stopped < rotStoppedSteps {
		return fmt.Errorf("rot progress stopped for only %d of %d window steps (%.0f rot ticks over %d game ticks)", stopped, rotWindowSteps, total, ticks)
	}
	if ticks <= 0 || total > rotWindowMaxFraction*float64(ticks) {
		return fmt.Errorf("rot progress advanced %.0f over %d game ticks of the frozen window, more than %.0f%% of the warm rate", total, ticks, 100*rotWindowMaxFraction)
	}
	return nil
}

type rotSummary struct {
	tick        int64
	destroyed   bool
	stage       string
	progress    float64
	rotTicks    float64
	temperature float64
	pawnsInRoom int
}

func (r rotSummary) evidence() map[string]any {
	return map[string]any{"tick": r.tick, "destroyed": r.destroyed, "stage": r.stage, "rot_progress": r.progress, "rot_ticks": r.rotTicks, "temperature_c": r.temperature, "pawns_in_room": r.pawnsInRoom}
}

// readRot is the private test/refrigeration_rot probe over one thing: its
// CompRottable progress, stage, runway, ambient temperature and the pawns
// sharing its room at the game tick of the read.
func readRot(ctx context.Context, h *na.Harness, id, label string) (rotSummary, error) {
	reply, err := h.Call(ctx, label, "test/refrigeration_rot", map[string]any{"id": id})
	if err != nil {
		return rotSummary{}, err
	}
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return rotSummary{}, fmt.Errorf("%s: %s", label, na.AsString(reply["reason"]))
	}
	destroyed, _ := na.AsBool(reply["destroyed"])
	return rotSummary{
		tick: int64(na.AsNumber(reply["tick"])), destroyed: destroyed, stage: na.AsString(reply["stage"]),
		progress: na.AsNumber(reply["rotProgress"]), rotTicks: na.AsNumber(reply["rotTicks"]), temperature: na.AsNumber(reply["temperatureC"]),
		pawnsInRoom: int(na.AsNumber(reply["pawnsInRoom"])),
	}, nil
}

// checkStartupLog is the run's last assertion: no native error in the
// game's startup log.
func checkStartupLog(s cases.Session) error {
	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}

type foodSummary struct {
	rows          int
	roofed        int
	temperature   float64
	warmNutrition float64
	// chilledMaxC is the catalog's full_rot_rate_c.
	chilledMaxC float64
}

// evidence is the report-serialisable form: the struct fields stay private
// to the harness, so the JSON report needs an explicit map.
func (f foodSummary) evidence() map[string]any {
	return map[string]any{"rows": f.rows, "roofed": f.roofed, "warmest_temperature_c": f.temperature, "warm_nutrition": f.warmNutrition}
}

// readFoodStorage decodes the typed food-supply census the way the Go
// projection does and summarises the perishable roofed stock: the warmest
// measured temperature and the nutrition the refrigeration review would
// count as warm at-risk.
func readFoodStorage(ctx context.Context, h *na.Harness, identity map[string]any, label string) (foodSummary, error) {
	reply, err := h.Wire(ctx, label, "observations_read_colony_facts", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "planning": false,
	})
	if err != nil {
		return foodSummary{}, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return foodSummary{}, err
	}
	section, _ := na.AsMap(observed["foodSupply"])
	_, food, err := na.Outcome(section, "observed")
	if err != nil {
		return foodSummary{}, fmt.Errorf("%s: food supply unavailable: %w", label, err)
	}
	p := policy.DefaultFoodStoragePolicy()
	data, err := json.Marshal(identity)
	if err != nil {
		return foodSummary{}, err
	}
	id := &c.Identity{}
	if err := protojson.Unmarshal(data, id); err != nil {
		return foodSummary{}, err
	}
	catalog, err := h.Client.DefinitionCatalog(ctx, id)
	if err != nil {
		return foodSummary{}, fmt.Errorf("%s: %w", label, err)
	}
	s := foodSummary{temperature: math.Inf(-1), chilledMaxC: float64(catalog.Constants.FullRotRateC)}
	for _, raw := range na.AsSlice(food["stocks"]) {
		row, _ := na.AsMap(raw)
		perishable, _ := na.AsBool(row["perishable"])
		roofed, _ := na.AsBool(row["roofed"])
		if !perishable {
			continue
		}
		s.rows++
		if roofed {
			s.roofed++
		}
		t := na.AsNumber(row["temperatureC"])
		if _, present := row["temperatureC"]; present && t > s.temperature {
			s.temperature = t
		}
		ticks := na.AsNumber(row["rotTicks"])
		if roofed && present(row, "temperatureC") && t > s.chilledMaxC && ticks > 0 && ticks < p.SafeRotDays*60000 && na.RefID(row["room"]) != "" {
			s.warmNutrition += na.AsNumber(row["nutrition"])
		}
	}
	return s, nil
}

func present(m map[string]any, key string) bool { _, ok := m[key]; return ok }

type coolerRow struct {
	id     string
	x, z   int32
	target float64
}

func (c coolerRow) evidence() map[string]any {
	return map[string]any{"id": c.id, "x": c.x, "z": c.z, "target_c": c.target}
}

func readCoolers(ctx context.Context, h *na.Harness, identity map[string]any) ([]coolerRow, error) {
	reply, err := h.Wire(ctx, "coolers-after", "observations_list_buildings", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "defNames": []string{"Cooler"}, "statuses": []string{"built"},
	})
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, err
	}
	var rows []coolerRow
	for _, raw := range na.AsSlice(observed["buildings"]) {
		row, _ := na.AsMap(raw)
		building, _ := na.AsMap(row["building"])
		if na.AsString(building["defName"]) != "Cooler" || na.AsString(row["status"]) != o.BuildingStatus_BUILDING_STATUS_BUILT.String() {
			continue
		}
		position, _ := na.AsMap(building["position"])
		settings, _ := na.AsMap(row["settings"])
		rows = append(rows, coolerRow{id: na.AsString(building["id"]), x: int32(na.AsNumber(position["x"])), z: int32(na.AsNumber(position["z"])), target: na.AsNumber(settings["targetTemperatureC"])})
	}
	return rows, nil
}

// storeWait bounds the journal polls below: the shared stall budget, and
// the service exiting on its own ends a wait at once.
func storeWait(service *na.ServiceProcess) na.Wait {
	return na.Wait{Stall: na.StallBudget(), Terminal: service.Exited}
}

// waitLatch waits for the review to latch refrigeration with a bound goal.
func waitLatch(ctx context.Context, s *store.Store, service *na.ServiceProcess) (store.Rounds, error) {
	w := storeWait(service)
	var review store.Rounds
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		r, err := s.LoadRounds(ctx)
		if err != nil {
			return "", false, err
		}
		review = r
		if latchedWithGoal(r) {
			return "", true, nil
		}
		return na.Signature(fmt.Sprintf("%+v", r.Latches), len(r.Goals)), false, nil
	})
	if err != nil {
		return review, fmt.Errorf("review never latched refrigeration with a bound goal (revision %d, latches %+v): %w", review.Revision, review.Latches, err)
	}
	return review, nil
}

func latchedWithGoal(r store.Rounds) bool {
	if !r.Latches.Refrigeration {
		return false
	}
	for _, binding := range r.Goals {
		if binding.Need == policy.MaintainRefrigeration {
			return true
		}
	}
	return false
}

// waitRelease waits for native cooling to release the latch. Nothing in the
// review moves while the room cools, so the signature carries the review's
// tick: a running game never reads as stalled, a paused one still does, and
// the tick budget (two game days, ample for a cooler against a heat wave)
// bounds the cooling itself.
func waitRelease(ctx context.Context, s *store.Store, service *na.ServiceProcess) (store.Rounds, error) {
	var review store.Rounds
	w := storeWait(service)
	w.Interval = time.Second
	w.Ticks = 2 * na.TicksPerDay
	w.Tick = func(ctx context.Context) (uint64, error) {
		r, err := s.LoadRounds(ctx)
		if err != nil {
			return 0, err
		}
		return uint64(r.Tick), nil
	}
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		r, err := s.LoadRounds(ctx)
		if err != nil {
			return "", false, err
		}
		review = r
		if !r.Latches.Refrigeration {
			return "", true, nil
		}
		return na.Signature(fmt.Sprintf("%+v", r.Latches), r.Revision, r.Tick), false, nil
	})
	if err != nil {
		return review, fmt.Errorf("review never released the refrigeration latch (revision %d): %w", review.Revision, err)
	}
	return review, nil
}

// refrigerationMethods lists every method ever committed on a
// MaintainRefrigeration goal, across Episodes, from the journal.
func refrigerationMethods(ctx context.Context, s *store.Store) ([]domain.GoalMethod, error) {
	review, err := s.LoadRounds(ctx)
	if err != nil {
		return nil, err
	}
	var out []domain.GoalMethod
	for _, binding := range review.Goals {
		if binding.Need != policy.MaintainRefrigeration {
			continue
		}
		goal, err := s.LoadStandard(ctx, binding.Goal)
		if err != nil {
			return nil, err
		}
		out = append(out, goal.Methods...)
	}
	return out, nil
}
