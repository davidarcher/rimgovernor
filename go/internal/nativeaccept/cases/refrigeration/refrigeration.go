// Package refrigeration holds the MaintainRefrigeration vertical (issue #6
// slice 1, milestone B): a live game and a live rimgovernor Go
// player-control service composed with the refrigeration family (and, for
// the power hand-off scenario, the power family too), one case per
// scenario:
//
//	build    -- an enclosed roofed stockpile room holds warm raw meat and has
//	            no cooler. The service must admit exactly one Cooler on a
//	            wall cell of that room with its hot side outdoors, the
//	            colonists build it, and native cooling then takes the
//	            measured room temperature under the review's exit threshold.
//	setpoint -- the room already has a powered, outward-facing cooler at a
//	            warm setpoint. The service must patch that cooler's target
//	            through the building-temperature CAS action rather than
//	            build a second one, and the room must cool.
//	power    -- the "hot-weather freezer failure": the existing cooler's
//	            conduit run to the generator is missing. The refrigeration
//	            family must hold (the cooler is unpowered, the power family's
//	            problem), the power family must route conduits so the cooler
//	            is powered, and only then the setpoint patch and cooling
//	            follow.
//	season   -- seasonal demand (#160): the storeroom starts settled cold
//	            with the fixture cooler idling at its warm setpoint and
//	            nothing at risk; heat waves ramp the outdoors up from the
//	            current tick and the harness runs the game until the cold
//	            room has followed and the stock reads warm (the native
//	            season turn, not a forced room), then starts the service,
//	            whose review must latch on the warmed stock, patch the
//	            setpoint down to the freezer target and release once native
//	            cooling holds -- with the cooler count still one.
//	season-second-cooler
//	         -- the freezer already sits at the controller's own target and
//	            the fixture's heater in the room (#220) outputs as much heat
//	            as one cooler removes, so the room warms to the outdoors and
//	            no setpoint patch can help. The epoch has no cooler method,
//	            so the review lends the cooling allowance from the tick its
//	            latch engaged (#202); once those two game days elapse
//	            unchilled the planner admits a second Cooler on another
//	            vented wall, and native cooling must then win: the cooler
//	            count goes from one to two and the room recovers. Raw meat
//	            would rot inside those two days, so the room stocks raw
//	            potatoes with every stack part-way to rotting: four days of
//	            runway, inside the review's at-risk bound and past the wait.
//	            The room is 8x6 rather than 6x4: a cooler's change per rare
//	            tick is capped at the gap to its setpoint, and in the small
//	            room two coolers nearing the freezer target remove less than
//	            the heater adds, settling just above 0 C where rot still runs.
//
// Every scenario ends with spoilage recovery: the fixture seeds one meat
// stack part-way to rotting, and once the room is chilled the case advances
// the game until that stack measures under 0 C and then over a further
// window in which its CompRottable progress must not move (issue #159).
//
// Uses the private disposable test/refrigeration_prepare fixture
// (RefrigerationFixture.cs) since a naturally generated colony never starts
// with an enclosed stockpile, Cooler research and a hot room together. The
// case's own bridge session and the service's are used sequentially, never
// concurrently (one GABP client per game).
package refrigeration

import (
	"context"
	"encoding/json"
	"fmt"
	"math"
	"os"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const prefix = "refrigeration-accept"

func init() {
	for _, scenario := range []string{"build", "setpoint", "power", "season", "season-second-cooler"} {
		scenario := scenario
		roomC, coolerTargetC, budget := 30, 21.0, 8*time.Minute
		food, rotStacks, rotFraction := "Meat_Muffalo", 1, 0.25
		roomWidth, roomHeight := 6, 4
		if scenario == "season" {
			roomC = 0
		}
		if scenario == "season-second-cooler" {
			// Two game days of lent allowance at one-hour windows before the
			// second cooler is even proposed; RawPotatoes rot in 30 days, so
			// 26/30 of the way leaves four days of warm runway.
			roomC, coolerTargetC, budget = 0, policy.DefaultFoodStoragePolicy().FreezerTargetC, cases.MaxBudget
			food, rotStacks, rotFraction = "RawPotatoes", 3, 26.0/30
			roomWidth, roomHeight = 8, 6
		}
		cases.Register(cases.Case{
			Name: "refrigeration/" + scenario,
			Scope: "Native MaintainRefrigeration vertical (" + scenario + "): warm at-risk meat in an enclosed room " +
				"drives the live Go routine reviewer/planner to admit a Cooler on a vented wall, patch an existing cooler's " +
				"setpoint, or hold for the power family; or a settled cold room warms as heat waves ramp in and the live " +
				"review latches and patches the idle cooler's setpoint; native cooling then takes the measured room under " +
				"the exit threshold, confirmed by an independent native read, and the seeded rotting stack's rot progress " +
				"stops advancing; or a freezer at target that a heater in the room defeats waits out the lent cooling allowance and " +
				"gains a second cooler (#220).",
			Start: cases.Fixture{Op: "test/refrigeration_prepare", Args: map[string]any{
				"existingCooler": scenario != "build", "disconnected": scenario == "power", "roomTemperatureC": roomC, "season": strings.HasPrefix(scenario, "season"),
				"coolerTargetC": coolerTargetC, "heater": scenario == "season-second-cooler", "foodDef": food, "rotStacks": rotStacks, "rotProgressFraction": rotFraction,
				"roomWidth": roomWidth, "roomHeight": roomHeight,
			}},
			Service: true,
			Budget:  budget,
			Run:     func(ctx context.Context, s cases.Session) error { return run(ctx, s, scenario) },
		})
	}
}

func run(ctx context.Context, s cases.Session, scenario string) error {
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
			"scope": map[string]any{"expectedIdentity": identity}, "colonists": false, "threats": true, "colonistDetail": false, "page": map[string]any{"limit": 256},
		}); err == nil {
			if _, observed, err := na.Outcome(reply, "observed"); err == nil {
				report["threats_postmortem"] = observed["threats"]
			}
		}
	}()
	if _, err := na.ConfirmColonyNames(ctx, h, report); err != nil {
		return err
	}
	coolerID := na.AsString(prepared["cooler"])
	interior, _ := na.AsMap(prepared["interior"])
	spare, _ := na.AsMap(prepared["spareCell"])
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
	// on -- or, for the season scenario, settled cold with nothing at risk.
	before, err := readFoodStorage(ctx, h, identity, "food-before")
	if err != nil {
		return err
	}
	report["food_before"] = before.evidence()
	policyDefaults := policy.DefaultFoodStoragePolicy()
	// The second-cooler scenario is a season row whose one refrigeration
	// method is a Cooler build, so it takes the season warm-up and the
	// build scenario's plan assertions.
	season, build := strings.HasPrefix(scenario, "season"), scenario == "build" || scenario == "season-second-cooler"
	if season {
		if before.rows == 0 || before.temperature > policyDefaults.ChilledMaxC || before.warmNutrition != 0 {
			return fmt.Errorf("food-before: fixture meat is not settled cold stock (warm nutrition %.2f, temperature %.1f C, rows %d)", before.warmNutrition, before.temperature, before.rows)
		}
		// The season turn is the game's own: the heat waves lerp the
		// outdoors up over 12000 ticks and the room equalises behind them
		// (the idle cooler holds nothing below its warm setpoint), so run
		// until the stock reads warm at-risk, bounded in game days.
		var warmed foodSummary
		advanced, err := na.RunUntil(ctx, h, "season-warm", 3*na.TicksPerDay, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
			f, err := readFoodStorage(ctx, h, identity, "season-warm-food")
			if err != nil {
				return "", false, err
			}
			warmed = f
			return na.Signature(int(f.temperature), int(f.warmNutrition)), f.warmNutrition >= policyDefaults.AtRiskNutritionThreshold, nil
		})
		if err != nil {
			return fmt.Errorf("season-warm: the room never warmed the stock past the at-risk threshold (warm nutrition %.2f, temperature %.1f C): %w", warmed.warmNutrition, warmed.temperature, err)
		}
		before = warmed
		report["season_warm_ticks"] = advanced
		report["food_warm"] = warmed.evidence()
	} else if before.warmNutrition < policyDefaults.AtRiskNutritionThreshold {
		return fmt.Errorf("food-before: fixture meat is not warm at-risk stock (warm nutrition %.2f, temperature %.1f C, roofed %d/%d); a cold biome may have overwhelmed the forced room temperature -- rerun",
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
	families := []string{"refrigeration", "work"}
	extra := na.ClockSpeedArgs()
	if scenario == "power" {
		families = append(families, "power")
	}
	service, err = s.Launch(ctx, na.ServiceLaunch{Families: families, Extra: extra})
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
	rootPlanID, err := service.SubmitAndResume(prefix, identity, map[string]any{
		"defName": "Wall", "x": int(na.AsNumber(spare["x"])), "z": int(na.AsNumber(spare["z"])), "rotation": "north", "stuff": "WoodLog",
	}, token, report)
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
	review, diagnostics, err := service.WaitRoutineReview(ctx, journal, 90*time.Second)
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

	if scenario == "power" {
		// The refrigeration family must not commit anything while the cooler
		// is unpowered; the power family's conduit method lands first.
		powerCtx, powerCancel := context.WithTimeout(ctx, 8*time.Minute)
		defer powerCancel()
		_, conduit, err := na.WaitGoalMethod(powerCtx, journal, policy.EnsureBasicPower, nil)
		if err != nil {
			return fmt.Errorf("power family conduit method: %w", err)
		}
		plan, err := journal.LoadPlan(ctx, conduit.Plan)
		if err != nil {
			return err
		}
		for _, action := range plan.Spec.Actions() {
			b, ok := action.Building()
			if !ok || b.Definition() != "PowerConduit" {
				return fmt.Errorf("power family committed a non-conduit action: %#v", action)
			}
		}
		report["power_conduit_plan"] = string(conduit.Plan)
		if fridge, err := refrigerationMethods(ctx, journal); err != nil {
			return err
		} else if len(fridge) != 0 {
			return fmt.Errorf("refrigeration committed %d methods while its cooler was unpowered: %#v", len(fridge), fridge)
		}
		// The evidence is the cooler regaining power, not every conduit
		// landing: the planner traces a path with slack, and once enough of
		// it is built the deficit clears and the rest never dispatches. The
		// refrigeration family commits its setpoint only once the cooler
		// reads powered, so a committed method ends the wait too.
		var completed int
		var completedTick uint64
		err = na.WaitProgress(powerCtx, na.Wait{Stall: na.StallBudget(), Interval: time.Second}, func(ctx context.Context) (string, bool, error) {
			state, err := journal.LoadPlan(ctx, conduit.Plan)
			if err != nil {
				return "", false, err
			}
			completed = 0
			terminal := len(state.Progress) > 0
			var signature []any
			for _, progress := range state.Progress {
				view := progress.View()
				signature = append(signature, view.Stage, view.Attempt, view.Unresolved)
				switch view.Stage {
				case domain.Completed:
					completed++
					if uint64(view.Tick) > completedTick {
						completedTick = uint64(view.Tick)
					}
				case domain.Unsuccessful:
					return "", false, fmt.Errorf("conduit plan %s reached unsuccessful instead of completed", conduit.Plan)
				default:
					terminal = false
				}
			}
			if terminal {
				return "", true, nil
			}
			fridge, err := refrigerationMethods(ctx, journal)
			if err != nil {
				return "", false, err
			}
			if completed > 0 && len(fridge) != 0 {
				return "", true, nil
			}
			return na.Signature(signature...), false, nil
		})
		if err != nil {
			return fmt.Errorf("conduit plan: %w", err)
		}
		report["power_conduits_completed"] = completed
		report["power_conduit_completed_tick"] = int64(completedTick)
	}

	// The refrigeration method: a Cooler build on a wall cell (build,
	// season-second-cooler) or a building-temperature patch of the fixture
	// cooler (setpoint, power, season).
	methodCtx, methodCancel := context.WithTimeout(ctx, 8*time.Minute)
	if scenario == "season-second-cooler" {
		// Nothing in the journal but the clock moves for two game days
		// while the lent allowance elapses: the admitted windows are the
		// progress signal, bounded by the allowance in ticks.
		methodCancel()
		methodCtx, methodCancel = context.WithTimeout(ctx, 11*time.Minute)
		lentTicks, err := waitLentAllowance(methodCtx, journal, service, latched.Latches.RefrigerationSince)
		report["lent_allowance_ticks"] = int64(lentTicks)
		if err != nil {
			methodCancel()
			return fmt.Errorf("second cooler after the lent allowance: %w", err)
		}
	}
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
		if build {
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
		} else {
			patch, ok := actions[0].BuildingTemperature()
			if !ok || patch.Thing() != coolerID {
				return fmt.Errorf("refrigeration plan action is not a temperature patch of %s: %#v", coolerID, actions[0])
			}
			if patch.Celsius() > policyDefaults.FreezerTargetC {
				return fmt.Errorf("setpoint patch targets %.1f C, above the freezer target %.1f C", patch.Celsius(), policyDefaults.FreezerTargetC)
			}
			report["setpoint_patch"] = map[string]any{"cooler": patch.Thing(), "celsius": patch.Celsius()}
		}
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
	if after.rows == 0 || after.temperature > policyDefaults.ChilledMaxC || after.warmNutrition > 0 {
		return fmt.Errorf("food-after: meat temperature %.1f C / warm nutrition %.2f is not chilled under %.1f C (rows %d)", after.temperature, after.warmNutrition, policyDefaults.ChilledMaxC, after.rows)
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
	expectedCoolers := 1
	if scenario == "season-second-cooler" {
		expectedCoolers = 2
	}
	if len(coolers) != expectedCoolers {
		return fmt.Errorf("expected exactly %d Cooler(s) after the run, observed %d: %#v", expectedCoolers, len(coolers), coolers)
	}
	var fixtureCooler, builtCooler bool
	for _, c := range coolers {
		switch {
		case coolerID != "" && c.id == coolerID:
			fixtureCooler = true
		case build && c.x == builtCell.X && c.z == builtCell.Z:
			builtCooler = true
		default:
			return fmt.Errorf("cooler %s at (%d,%d) is neither the fixture cooler %s nor the admitted cell %v", c.id, c.x, c.z, coolerID, builtCell)
		}
		if c.target > policyDefaults.FreezerTargetC {
			return fmt.Errorf("cooler %s target %.1f C is above the freezer target %.1f C", c.id, c.target, policyDefaults.FreezerTargetC)
		}
	}
	if coolerID != "" && !fixtureCooler {
		return fmt.Errorf("the fixture cooler %s did not survive the run: %#v", coolerID, coolers)
	}
	if build && !builtCooler {
		return fmt.Errorf("no built cooler sits at the admitted cell %v: %#v", builtCell, coolers)
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
		"scope": map[string]any{"expectedIdentity": identity}, "planning": false, "page": map[string]any{"limit": 256},
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
	s := foodSummary{temperature: math.Inf(-1)}
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
		if roofed && present(row, "temperatureC") && t > p.ChilledMaxC && ticks > 0 && ticks < p.SafeRotDays*60000 && na.AsString(row["roomId"]) != "" {
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
		"scope": map[string]any{"expectedIdentity": identity}, "defNames": []string{"Cooler"}, "statuses": []string{"built"}, "page": map[string]any{"limit": 64},
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
		if na.AsString(building["defName"]) != "Cooler" || na.AsString(row["status"]) != "built" {
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

// waitLentAllowance follows the epoch that has no cooler method while the
// review lends the cooling allowance from its latch tick (#202): the
// journal's admitted clock windows are the progress signal, and the wait
// ends when the goal gains its first method. A window admitted more than
// twice the allowance past the latch without one is a failure in its own
// right: the allowance elapsed and no second cooler was proposed.
func waitLentAllowance(ctx context.Context, s *store.Store, service *na.ServiceProcess, since domain.Tick) (domain.Tick, error) {
	const allowance = 2 * na.TicksPerDay
	var latest domain.Tick
	err := na.WaitProgress(ctx, storeWait(service), func(ctx context.Context) (string, bool, error) {
		methods, err := refrigerationMethods(ctx, s)
		if err != nil {
			return "", false, err
		}
		if len(methods) > 0 {
			return "", true, nil
		}
		attempts, err := s.LoadClockAttempts(ctx, 4096)
		if err != nil {
			return "", false, err
		}
		for _, a := range attempts {
			if a.Intent.Window != nil && a.Intent.Window.Tick > latest {
				latest = a.Intent.Window.Tick
			}
		}
		if latest > since+2*allowance {
			return "", false, fmt.Errorf("a window was admitted at tick %d, %d ticks past the latch at %d, with no cooler method", latest, latest-since, since)
		}
		return na.Signature(latest), false, nil
	})
	return latest - since, err
}

// waitLatch waits for the review to latch refrigeration with a bound goal.
func waitLatch(ctx context.Context, s *store.Store, service *na.ServiceProcess) (store.RoutineReview, error) {
	w := storeWait(service)
	var review store.RoutineReview
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		r, err := s.LoadRoutineReview(ctx)
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

func latchedWithGoal(r store.RoutineReview) bool {
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
func waitRelease(ctx context.Context, s *store.Store, service *na.ServiceProcess) (store.RoutineReview, error) {
	var review store.RoutineReview
	w := storeWait(service)
	w.Interval = time.Second
	w.Ticks = 2 * na.TicksPerDay
	w.Tick = func(ctx context.Context) (uint64, error) {
		r, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return 0, err
		}
		return uint64(r.Tick), nil
	}
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		r, err := s.LoadRoutineReview(ctx)
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
// MaintainRefrigeration goal, across goal epochs, from the journal.
func refrigerationMethods(ctx context.Context, s *store.Store) ([]domain.GoalMethod, error) {
	review, err := s.LoadRoutineReview(ctx)
	if err != nil {
		return nil, err
	}
	var out []domain.GoalMethod
	for _, binding := range review.Goals {
		if binding.Need != policy.MaintainRefrigeration {
			continue
		}
		goal, err := s.LoadGoal(ctx, binding.Goal)
		if err != nil {
			return nil, err
		}
		out = append(out, goal.Methods...)
	}
	return out, nil
}
