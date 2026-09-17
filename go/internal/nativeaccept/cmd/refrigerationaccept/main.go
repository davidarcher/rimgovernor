// Command refrigerationaccept exercises the MaintainRefrigeration vertical
// (issue #6 slice 1, milestone B) end to end against a live game and a live
// rimgovernor Go player-control service composed with the refrigeration
// family (and, for the power hand-off scenario, the power family too):
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
//
// Uses the private disposable test/refrigeration_prepare fixture
// (RefrigerationFixture.cs) since a naturally generated colony never starts
// with an enclosed stockpile, Cooler research and a hot room together. As
// with routinehaulaccept, the harness's own bridge session and the service's
// are used sequentially, never concurrently (one GABP client per game).
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"math"
	"os"
	"path/filepath"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const prefix = "refrigeration-accept"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-refrigeration-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	binary := flag.String("rimgovernor", "", "absolute path to a prebuilt rimgovernor binary (go build ./go/cmd/rimgovernor)")
	scenario := flag.String("scenario", "build", "build, setpoint or power")
	timeout := flag.Duration("timeout", 30*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" || *binary == "" || !filepath.IsAbs(*binary) {
		fmt.Fprintln(os.Stderr, "-root and an absolute -rimgovernor are required")
		os.Exit(2)
	}
	if *scenario != "build" && *scenario != "setpoint" && *scenario != "power" {
		fmt.Fprintln(os.Stderr, "-scenario must be build, setpoint or power")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-refrigeration-acceptance-" + *scenario
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	if entries, _ := os.ReadDir(*output); len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport("Native MaintainRefrigeration vertical ("+*scenario+"): warm at-risk meat in an enclosed room "+
		"drives the live Go routine reviewer/planner to admit a Cooler on a vented wall, patch an existing cooler's "+
		"setpoint, or hold for the power family; native cooling then takes the measured room under the exit "+
		"threshold, confirmed by an independent native read.", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, !*rendered, *binary, *scenario, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID string, headless bool, binary, scenario string, report na.Report) error {
	if abs, err := filepath.Abs(root); err == nil {
		root = abs
	}
	if abs, err := filepath.Abs(output); err == nil {
		output = abs
	}
	cfg := &na.Config{Root: root, Output: output, Headless: headless, GameID: gameID}
	if err := cfg.PrepareConfig(); err != nil {
		return fmt.Errorf("prepare profile: %w", err)
	}
	game, err := cfg.GameSection()
	if err != nil {
		return err
	}
	files, err := na.PackageFiles(fmt.Sprint(game["workingDir"]))
	if err != nil {
		return err
	}
	report["package_files"] = files
	gabsExecutable, err := na.GABSExecutable(root, cfg.Configuration)
	if err != nil {
		return err
	}
	client, err := na.OpenSession(ctx, gabsExecutable, cfg.Configuration, gameID, 120*time.Second)
	if err != nil {
		return err
	}
	h := na.NewHarness(client, output)
	// sessionOpen tracks whether client is usable: the harness closes its
	// GABS session while the rimgovernor service owns the game slot and
	// reopens one afterwards. On any failure in between, stopGame must stop
	// the service and reopen a session first, or games_stop fails with
	// "bridge closed" and the disposable game outlives the run (observed:
	// the orphan then made the next run's start_debug_game_ready time out).
	sessionOpen := true
	var service *na.ServiceProcess
	// postmortem is the identity the failure path reads the food census
	// under once the fixture exists; nil until then.
	var postmortem map[string]any
	stopped := false
	stopGame := func() {
		if stopped {
			return
		}
		stopped = true
		if service != nil {
			service.Stop()
		}
		stopCtx, stopCancel := context.WithTimeout(context.Background(), 90*time.Second)
		defer stopCancel()
		if !sessionOpen {
			reopened, err := na.ReopenSession(stopCtx, gabsExecutable, cfg.Configuration, gameID)
			if err != nil {
				report["stop_error"] = "reopen session for games_stop: " + err.Error()
				return
			}
			client, sessionOpen = reopened, true
			// Failure evidence: the same independent food read a pass ends
			// with, so a dropped refrigeration latch can be explained
			// (stock hauled out, rotted, or genuinely chilled).
			if postmortem != nil {
				if _, hasAfter := report["food_after"]; !hasAfter {
					ph := na.NewHarness(client, output)
					if after, err := readFoodStorage(stopCtx, ph, postmortem, "food-postmortem"); err == nil {
						report["food_postmortem"] = after.evidence()
					} else {
						report["food_postmortem_error"] = err.Error()
					}
					// The emergency reviewer's own threat census, so an
					// unsafe_colony hold names the pawns behind it.
					if reply, err := ph.Wire(stopCtx, "threats-postmortem", "observations_read_status", map[string]any{
						"scope": map[string]any{"expectedIdentity": postmortem}, "colonists": false, "threats": true, "colonistDetail": false, "page": map[string]any{"limit": 256},
					}); err == nil {
						if _, observed, err := na.Outcome(reply, "observed"); err == nil {
							report["threats_postmortem"] = observed["threats"]
						}
					}
				}
			}
		}
		if s, err := client.GamesStop(stopCtx); err == nil {
			report["stop"] = string(s.Envelope)
		} else {
			report["stop_error"] = err.Error()
		}
		_ = client.Close()
	}
	defer stopGame()

	if _, err := na.StartDebugGame(ctx, h, nil, na.QuietRequired); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	if err := confirmColonyNames(ctx, h, report); err != nil {
		return err
	}
	identityReply, err := h.Wire(ctx, "identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, loaded, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return err
	}
	loadedContext, _ := na.AsMap(loaded["context"])
	identity, _ := na.AsMap(loadedContext["identity"])
	postmortem = identity
	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	report["discovery"] = names
	if !na.Contains(names, "test/refrigeration_prepare") {
		return fmt.Errorf("missing test/refrigeration_prepare in discovery; rebuild the native mod with -Fixture RefrigerationFixture")
	}

	prepared, err := h.Call(ctx, "prepare", "test/refrigeration_prepare", map[string]any{
		"existingCooler": scenario != "build", "disconnected": scenario == "power", "roomTemperatureC": 30,
	})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(prepared["success"]); !success || !na.MatchesIdentity(prepared, identity) {
		return fmt.Errorf("refrigeration_prepare refused or identity mismatch: %#v", prepared)
	}
	report["prepared"] = prepared
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
	// fixture room and short of runway -- the exact facts the review latches on.
	before, err := readFoodStorage(ctx, h, identity, "food-before")
	if err != nil {
		return err
	}
	report["food_before"] = before.evidence()
	policyDefaults := policy.DefaultFoodStoragePolicy()
	if before.warmNutrition < policyDefaults.AtRiskNutritionThreshold {
		return fmt.Errorf("food-before: fixture meat is not warm at-risk stock (warm nutrition %.2f, temperature %.1f C, roofed %d/%d); a cold biome may have overwhelmed the forced room temperature -- rerun",
			before.warmNutrition, before.temperature, before.roofed, before.rows)
	}
	if err := client.Close(); err != nil {
		return fmt.Errorf("close fixture-prep bridge session: %w", err)
	}
	sessionOpen = false

	// "work" rides along because every building method's builder check
	// (comfortBuilderAvailable) requires the colony's work priorities to match
	// the controller's own assignment, which only the work family applies.
	families := []string{"refrigeration", "work"}
	if scenario == "power" {
		families = append(families, "power")
	}
	service, err = na.LaunchService(ctx, cfg, gabsExecutable, na.ServiceLaunch{Binary: binary, Families: families, Extra: []string{"--clock-speed", "Fast"}}, report)
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
	latched, err := waitLatch(waitCtx, journal)
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
		done, _, err := na.WaitPlanTerminal(powerCtx, journal, conduit.Plan)
		if err != nil {
			return fmt.Errorf("conduit plan: %w", err)
		}
		report["power_conduit_completed_tick"] = int64(done.Progress[len(done.Progress)-1].View().Tick)
	}

	// The refrigeration method: a Cooler build on a wall cell (build) or a
	// building-temperature patch of the fixture cooler (setpoint, power).
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
		if scenario == "build" {
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
	released, err := waitRelease(coolCtx, journal)
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
	client, err = na.ReopenSession(ctx, gabsExecutable, cfg.Configuration, gameID)
	if err != nil {
		return fmt.Errorf("reopen harness session after service stop: %w", err)
	}
	sessionOpen = true
	h = na.NewHarness(client, output)
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
	if len(coolers) != 1 {
		return fmt.Errorf("expected exactly one Cooler after the run, observed %d: %#v", len(coolers), coolers)
	}
	c := coolers[0]
	if scenario != "build" && c.id != coolerID {
		return fmt.Errorf("the surviving cooler %s is not the fixture cooler %s", c.id, coolerID)
	}
	if scenario == "build" && (c.x != builtCell.X || c.z != builtCell.Z) {
		return fmt.Errorf("the built cooler sits at (%d,%d), not the admitted cell %v", c.x, c.z, builtCell)
	}
	if c.target > policyDefaults.FreezerTargetC {
		return fmt.Errorf("cooler target %.1f C is above the freezer target %.1f C", c.target, policyDefaults.FreezerTargetC)
	}
	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}

// confirmColonyNames dismisses the fresh debug game's naming dialog, which
// RankDevelopment otherwise treats as a global emergency (see routinehaulaccept).
func confirmColonyNames(ctx context.Context, h *na.Harness, report na.Report) error {
	facts, err := h.Call(ctx, "colony-facts", "home/colony_facts", map[string]any{})
	if err != nil {
		return err
	}
	naming, ok := na.AsMap(facts["colonyNaming"])
	if !ok || naming == nil {
		report["confirmed_colony_names"] = "no pending naming dialog"
		return nil
	}
	confirmed, err := h.Call(ctx, "confirm-colony-names", "home/confirm_colony_names", map[string]any{
		"windowId": int(na.AsNumber(naming["windowId"])), "factionName": na.AsString(naming["factionName"]),
		"settlementName": na.AsString(naming["settlementName"]), "dryRun": false,
	})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(confirmed["success"]); !success {
		return fmt.Errorf("confirm_colony_names refused: %#v", confirmed)
	}
	report["confirmed_colony_names"] = confirmed
	return nil
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

func waitLatch(ctx context.Context, s *store.Store) (store.RoutineReview, error) {
	for {
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return review, err
		}
		if review.Latches.Refrigeration {
			for _, binding := range review.Goals {
				if binding.Need == policy.MaintainRefrigeration {
					return review, nil
				}
			}
		}
		select {
		case <-ctx.Done():
			return review, fmt.Errorf("review never latched refrigeration with a bound goal (revision %d, latches %+v): %w", review.Revision, review.Latches, ctx.Err())
		case <-time.After(time.Second):
		}
	}
}

func waitRelease(ctx context.Context, s *store.Store) (store.RoutineReview, error) {
	for {
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return review, err
		}
		if !review.Latches.Refrigeration {
			return review, nil
		}
		select {
		case <-ctx.Done():
			return review, fmt.Errorf("review never released the refrigeration latch (revision %d): %w", review.Revision, ctx.Err())
		case <-time.After(2 * time.Second):
		}
	}
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
