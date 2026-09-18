// Command upkeepaccept is issue #2's (B04h) colony-upkeep acceptance: one
// staged deficit per scenario, the live rimgovernor service composed with
// only the routine families that own it, the durable journal followed from
// deficit through method to observed recovery, and an independent native
// read after the service releases the game. Every scenario opens on a
// fixture that already holds the deficit; none plays a colony into it.
//
//	scattered       -- test/upkeep_setup: herbal medicine lying outdoors beside
//	                   a covered stockpile that accepts it, a half-damaged home
//	                   wall and outdoor dirt. SecureSupplies must haul the
//	                   medicine into covered storage and MaintainEssentialRepairs
//	                   must restore the wall, each recovering only on the
//	                   observed postcondition; the dirt is outside any workspace,
//	                   so MaintainCleanFacilities never issues an order.
//	storage-missing -- the same fixture with the covered cells left unzoned: no
//	                   storage accepts the medicine, so ordinary hauling is
//	                   refused and SecureSupplies falls back to creating one
//	                   filtered stockpile on the covered cells before hauling.
//	blocked         -- the same fixture walled off from every worker (forced
//	                   orders ignore allowed areas, so only pathing blocks
//	                   them): no haul or repair may complete,
//	                   the medicine must stay where it is, and the deficits
//	                   remain visible rather than silently dropped.
//	fire            -- the same fixture with one small home fire: the fire goal
//	                   latches as an emergency that defers every development
//	                   row, native firefighting/burnout clears it, and the goal
//	                   recovers only once no home fire remains.
//	medicine        -- test/medicine_setup: every medicine destroyed and mature
//	                   wild healroot nearby. MaintainMedicalReserves must
//	                   replenish through ordinary plant cutting until the
//	                   recovery reserve is observed in stock.
//	feed            -- test/feed_setup: a hungry pet confined to a small area,
//	                   a butcher spot and meat/hay stock outside its reach.
//	                   MaintainAnimalFeed must produce reachable feed (a kibble
//	                   bill) rather than counting stock the animal cannot eat.
//	sleeping        -- test/sleeping_setup: a warm roofed room holding one bed
//	                   fewer than colonists, wood and bed research.
//	                   MaintainSleeping must build the missing bed, ownership
//	                   must follow (the controller's AssignBed or the colonist's
//	                   own claim), and the goal recovers only once every
//	                   colonist is observed sleeping in an owned bed.
//	cold            -- test/routine_sleeping_prepare + routine_temperature_prepare:
//	                   sleeping spots in an enclosed room at a sub-12 C outdoor
//	                   temperature with campfire materials. EnsureTemperatureSafety
//	                   must place a heat source and recover on the measured
//	                   sleeping temperature. Fails fast when the rolled map is
//	                   too warm for the fixture (rerun as a fresh process).
//
// The harness's own bridge session and the service's are used sequentially,
// never concurrently (one GABP slot). Needs the native mod built with
// -Fixture UpkeepFixture,ForecastFixture,RoutineSleepingFixture.
//
// -scenario takes a comma-separated list (or "all"); each scenario then runs
// under <output>/<scenario> with a summary result.json at <output>. -reuse
// launches RimWorld once and reloads the tribal8 baseline save for every
// scenario (issue #22's GameReuse), so the multi-minute boot is paid once;
// a failed or unclean case retires the game and the next scenario relaunches.
package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const prefix = "upkeep-accept"

// scenario stages one deficit, names the families that own it, follows the
// journal to recovery and audits the native outcome.
type scenario struct {
	name     string
	fixture  string
	families []string
	extra    []string
	prepare  func(ctx context.Context, h *na.Harness, identity map[string]any, report na.Report) (map[string]any, error)
	watch    func(ctx context.Context, journal *store.Store, prepared map[string]any, report na.Report) error
	verify   func(ctx context.Context, h *na.Harness, identity, prepared map[string]any, report na.Report) error
}

func scenarios() map[string]*scenario {
	upkeep := func(name string, args map[string]any) *scenario {
		// The fixture clears the workers' hauling/cleaning/construction
		// priorities, so nobody works the staged deficit until the "work"
		// family assigns priorities; development ranking defers both goals
		// as labor_unavailable until then. From that point the controller's
		// order and ordinary colonist work race; followMethods accepts both.
		return &scenario{name: name, fixture: "test/upkeep_setup",
			families: []string{"secure-supplies", "repair", "clean", "work"},
			extra:    []string{"--routine-project-limit", "4"},
			prepare: func(ctx context.Context, h *na.Harness, identity map[string]any, report na.Report) (map[string]any, error) {
				return callFixture(ctx, h, identity, "test/upkeep_setup", args)
			},
		}
	}
	s := map[string]*scenario{}

	scattered := upkeep("scattered", map[string]any{})
	scattered.watch = watchScattered
	scattered.verify = verifyScattered
	s["scattered"] = scattered

	missing := upkeep("storage-missing", map[string]any{"storageMissing": true})
	missing.watch = watchStorageMissing
	missing.verify = verifyStorageMissing
	s["storage-missing"] = missing

	blocked := upkeep("blocked", map[string]any{"restrictWorkers": true})
	blocked.watch = watchBlocked
	blocked.verify = verifyBlocked
	s["blocked"] = blocked

	fire := upkeep("fire", map[string]any{"fireSize": 0.9})
	// MaintainFireSafety's method is a clock window for native firefighting;
	// without the fire family nothing ever admits one (refused=[no_work]).
	fire.families = append([]string{"fire"}, fire.families...)
	fire.watch = watchFire
	fire.verify = verifyFire
	s["fire"] = fire

	s["medicine"] = &scenario{name: "medicine", fixture: "test/medicine_setup",
		families: []string{"medical", "resource", "bill", "acquisition", "work"},
		prepare: func(ctx context.Context, h *na.Harness, identity map[string]any, report na.Report) (map[string]any, error) {
			return callFixture(ctx, h, identity, "test/medicine_setup", map[string]any{})
		},
		watch:  watchMedicine,
		verify: verifyMedicine,
	}
	s["feed"] = &scenario{name: "feed", fixture: "test/feed_setup",
		families: []string{"animal-feed", "resource", "bill", "work"},
		prepare: func(ctx context.Context, h *na.Harness, identity map[string]any, report na.Report) (map[string]any, error) {
			return callFixture(ctx, h, identity, "test/feed_setup", map[string]any{})
		},
		watch:  watchFeed,
		verify: verifyFeed,
	}
	s["sleeping"] = &scenario{name: "sleeping", fixture: "test/sleeping_setup",
		families: []string{"sleeping", "work"},
		prepare: func(ctx context.Context, h *na.Harness, identity map[string]any, report na.Report) (map[string]any, error) {
			prepared, err := callFixture(ctx, h, identity, "test/sleeping_setup", map[string]any{})
			if err != nil {
				return nil, err
			}
			report["fixture_owned_beds"] = len(na.AsSlice(prepared["ownedBeds"]))
			report["fixture_room_temperature_c"] = prepared["roomTemperatureC"]
			return prepared, nil
		},
		watch:  watchSleeping,
		verify: verifySleeping,
	}
	s["cold"] = &scenario{name: "cold", fixture: "test/routine_temperature_prepare",
		families: []string{"temperature", "work"},
		prepare:  prepareCold,
		watch:    watchCold,
		verify:   verifyCold,
	}
	return s
}

// reuseSave is the save every reuse case reloads before its fixture stages
// the deficit (issue #22): one launch per invocation instead of one per
// scenario, with GameReuse's reset contract guarding the shared process.
// Fresh-process mode keeps rimworld/start_debug_game_ready so a rolled map
// stays available for scenarios that want one.
const reuseSave = "RimGovernor-tribal8-baseline"

// scenarioOrder is the -scenario all expansion, cheapest deficit first.
var scenarioOrder = []string{"scattered", "storage-missing", "blocked", "fire", "medicine", "feed", "sleeping", "cold"}

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-upkeep-acceptance-<scenario>, or <root>/native-upkeep-acceptance for a multi-scenario run)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	binary := flag.String("rimgovernor", "", "absolute path to a prebuilt rimgovernor binary (go build ./go/cmd/rimgovernor)")
	names := flag.String("scenario", "scattered", "comma-separated list of scattered, storage-missing, blocked, fire, medicine, feed, sleeping, cold (or all)")
	timeout := flag.Duration("timeout", 40*time.Minute, "per-scenario timeout")
	debug := flag.Bool("debug", false, "record every native call (flight.jsonl) and the clock/worker diagnostic log")
	reuseGame := flag.Bool("reuse", false, "launch RimWorld once and reload "+reuseSave+" for every scenario (issue #22); a failed scenario retires the game and the next one relaunches")
	flag.Parse()
	if *root == "" || *binary == "" || !filepath.IsAbs(*binary) {
		fmt.Fprintln(os.Stderr, "-root and an absolute -rimgovernor are required")
		os.Exit(2)
	}
	all := scenarios()
	var selected []*scenario
	for _, name := range strings.Split(*names, ",") {
		name = strings.TrimSpace(name)
		if name == "all" {
			for _, n := range scenarioOrder {
				selected = append(selected, all[n])
			}
			continue
		}
		sc, ok := all[name]
		if !ok {
			fmt.Fprintln(os.Stderr, "-scenario must list scattered, storage-missing, blocked, fire, medicine, feed, sleeping or cold")
			os.Exit(2)
		}
		selected = append(selected, sc)
	}
	if len(selected) == 0 {
		fmt.Fprintln(os.Stderr, "-scenario names nothing")
		os.Exit(2)
	}
	if *output == "" {
		if len(selected) == 1 {
			*output = *root + "/native-upkeep-acceptance-" + selected[0].name
		} else {
			*output = *root + "/native-upkeep-acceptance"
		}
	}
	if abs, err := filepath.Abs(*output); err == nil {
		*output = abs
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	if entries, _ := os.ReadDir(*output); len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	if len(selected) == 1 && !*reuseGame {
		report := newScenarioReport(selected[0].name, !*rendered)
		ctx, cancel := context.WithTimeout(context.Background(), *timeout)
		defer cancel()
		err := run(ctx, *root, *output, *game, !*rendered, *binary, selected[0], *debug, report, nil)
		if err != nil {
			report["error"] = err.Error()
		} else {
			report["passed"] = true
		}
		os.Exit(report.Finalize(*output))
	}
	os.Exit(runMany(*root, *output, *game, !*rendered, *binary, selected, *debug, *reuseGame, *timeout))
}

func newScenarioReport(name string, headless bool) na.Report {
	report := na.NewReport("Native colony upkeep vertical ("+name+", issue #2 B04h): a staged deficit is reviewed, "+
		"its method dispatched through the live service, recovery observed on the native postcondition rather than a "+
		"receipt, and the outcome confirmed by an independent native read.", headless)
	report["scenario"] = name
	return report
}

// runMany runs each scenario under <output>/<scenario> with its own
// result.json and writes a summary result.json at output. With reuse, one
// game serves every scenario until a case fails or leaves the game unclean,
// after which the next scenario pays for a relaunch; without it every
// scenario is its own fresh process.
func runMany(root, output, gameID string, headless bool, binary string, selected []*scenario, debug, reuseGame bool, timeout time.Duration) int {
	summary := na.NewReport("Native colony upkeep matrix (issue #2 B04h): every listed scenario run in sequence, "+
		"each under its own directory with its own result.json.", headless)
	summary["reuse_game"] = reuseGame
	var rows []map[string]any
	var lifecycles []map[string]any
	passed := true
	var reuse *na.GameReuse
	retireReuse := func(reason string) {
		if reuse == nil {
			return
		}
		retireCtx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
		defer cancel()
		_ = reuse.Retire(retireCtx, reason)
		lifecycles = append(lifecycles, map[string]any{"rows": reuse.Rows()})
		reuse = nil
	}
	for _, sc := range selected {
		caseOutput := filepath.Join(output, sc.name)
		report := newScenarioReport(sc.name, headless)
		ctx, cancel := context.WithTimeout(context.Background(), timeout)
		if reuseGame && reuse == nil {
			cfg := &na.Config{Root: root, Output: output, Headless: headless, GameID: gameID}
			err := cfg.UseSaveExpansions(reuseSave)
			if err == nil {
				err = cfg.PrepareConfig()
			}
			if err != nil {
				report["error"] = "prepare reuse profile: " + err.Error()
			} else if gabsExecutable, err := na.GABSExecutable(root, cfg.Configuration); err != nil {
				report["error"] = err.Error()
			} else if opened, err := na.OpenReusableGame(ctx, cfg, gabsExecutable); err != nil {
				report["error"] = "open reusable game: " + err.Error()
			} else {
				reuse = opened
			}
		}
		if _, failed := report["error"]; !failed {
			if err := os.MkdirAll(caseOutput, 0755); err != nil {
				report["error"] = err.Error()
			} else if err := run(ctx, root, caseOutput, gameID, headless, binary, sc, debug, report, reuse); err != nil {
				report["error"] = err.Error()
			} else {
				report["passed"] = true
			}
		}
		cancel()
		code := report.Finalize(caseOutput)
		row := map[string]any{"scenario": sc.name, "passed": code == 0, "output": caseOutput}
		if err, ok := report["error"]; ok {
			row["error"] = err
		}
		if reuse != nil {
			if retired, reason := reuse.Retired(); retired {
				row["reuse_retired"] = reason
				retireReuse(reason)
			}
		}
		rows = append(rows, row)
		passed = passed && code == 0
	}
	retireReuse("matrix complete")
	summary["scenarios"] = rows
	summary["reuse"] = lifecycles
	summary["passed"] = passed
	return summary.Finalize(output)
}

// run executes one scenario. With reuse != nil the scenario is a reuse case:
// the shared game reloads the baseline save, the case borrows the shared
// harness session (released while the service owns the GABP slot) and
// EndCase verifies quiescence; the game is stopped only on retirement.
// Without it the run owns a fresh process from start_debug_game_ready to
// games_stop.
func run(ctx context.Context, root, output, gameID string, headless bool, binary string, sc *scenario, debug bool, report na.Report, reuse *na.GameReuse) (err error) {
	if abs, err := filepath.Abs(root); err == nil {
		root = abs
	}
	if abs, err := filepath.Abs(output); err == nil {
		output = abs
	}
	cfg := &na.Config{Root: root, Output: output, Headless: headless, GameID: gameID}
	if reuse != nil {
		// The shared lifecycle's profile, but this case's own output: the
		// service journal and profile must not leak into the next case.
		shared := *reuse.Config
		shared.Output = output
		cfg = &shared
	} else if err := cfg.PrepareConfig(); err != nil {
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

	var h *na.Harness
	var service *na.ServiceProcess
	var postmortem map[string]any
	// readPostmortem records the deficit facts as the game last saw them
	// when a scenario fails after the service took over the slot.
	readPostmortem := func(stopCtx context.Context, ph *na.Harness) {
		if postmortem == nil || ph == nil {
			return
		}
		if upkeep, err := readUpkeep(stopCtx, ph, postmortem, "upkeep-postmortem"); err == nil {
			report["upkeep_postmortem"] = upkeep
		} else {
			report["upkeep_postmortem_error"] = err.Error()
		}
	}
	// The fixture-prep session is released while the service owns the
	// single GABP slot and reacquired for the independent read afterwards.
	var releaseSession func() error
	var reacquireSession func() (*na.Harness, error)
	stopped := false
	if reuse != nil {
		// Assigned, not declared: the deferred EndCase reads the named
		// return to decide whether the case failed.
		var reuseCase *na.ReuseCase
		reuseCase, err = reuse.BeginCase(ctx, sc.name, reuseSave, output)
		if err != nil {
			return err
		}
		h = reuseCase.Harness
		report["reuse_case"] = map[string]any{"loadToken": reuseCase.Reset.LoadToken, "tick": reuseCase.Reset.Tick}
		releaseSession = reuse.ReleaseSession
		reacquireSession = func() (*na.Harness, error) {
			rh, err := reuse.Session(ctx)
			if err != nil {
				return nil, err
			}
			rh.Output = output
			return rh, nil
		}
		defer func() {
			if stopped {
				return
			}
			stopped = true
			if service != nil {
				service.Stop()
			}
			endCtx, endCancel := context.WithTimeout(context.Background(), 2*time.Minute)
			defer endCancel()
			if err != nil {
				if ph, sessionErr := reuse.Session(endCtx); sessionErr == nil {
					ph.Output = output
					readPostmortem(endCtx, ph)
				}
			}
			if endErr := reuse.EndCase(endCtx, reuseCase, err != nil); endErr != nil && err == nil {
				err = endErr
			}
		}()
	} else {
		held, err := na.OpenGame(ctx, cfg)
		if err != nil {
			return err
		}
		h = na.NewHarness(held.Client, output)
		releaseSession = held.Release
		reacquireSession = func() (*na.Harness, error) {
			reopened, err := held.Reattach(ctx)
			if err != nil {
				return nil, fmt.Errorf("reopen harness session after service stop: %w", err)
			}
			return na.NewHarness(reopened, output), nil
		}
		defer func() {
			if stopped {
				return
			}
			stopped = true
			if service != nil {
				service.Stop()
			}
			stopCtx, stopCancel := context.WithTimeout(context.Background(), 90*time.Second)
			defer stopCancel()
			if err != nil {
				if reopened, err := held.Reattach(stopCtx); err == nil {
					readPostmortem(stopCtx, na.NewHarness(reopened, output))
				}
			}
			held.Close(report)
		}()
		if _, err := h.Call(ctx, "new-game", "rimworld/start_debug_game_ready", map[string]any{
			"readiness": "visual", "pauseIfNeeded": true, "timeoutMs": 120000,
		}); err != nil {
			return err
		}
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
	if !na.Contains(names, sc.fixture) {
		return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture UpkeepFixture,ForecastFixture,RoutineSleepingFixture", sc.fixture)
	}
	prepared, err := sc.prepare(ctx, h, identity, report)
	if err != nil {
		return err
	}
	report["prepared"] = prepared
	before, err := readUpkeep(ctx, h, identity, "upkeep-before")
	if err != nil {
		return err
	}
	report["upkeep_before"] = before
	if err := releaseSession(); err != nil {
		return fmt.Errorf("close fixture-prep bridge session: %w", err)
	}

	// The clock speed follows RIMGOVERNOR_ACCEPT_CLOCK_SPEED like the other
	// serve-driven harnesses; Ultrafast adds the headless dev tick boost.
	extra := append(na.ClockSpeedArgs(), sc.extra...)
	var env []string
	if debug {
		extra = append(extra, "--flight-recorder", filepath.Join(output, "flight.jsonl"))
		env = []string{"RIMGOVERNOR_CLOCK_DEBUG=1"}
	}
	service, err = na.LaunchService(ctx, cfg, gabsExecutable, na.ServiceLaunch{Binary: binary, Families: sc.families, Extra: extra, Env: env}, report)
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
	// Any other emergency (an injured starting colonist, hostiles) holds the
	// clock and every development row, so the deficit under test could never
	// be dispatched; report the roll rather than time out.
	if sc.name != "fire" {
		for _, row := range review.Development.Rows {
			if row.Reason == policy.DevelopmentEmergency {
				return fmt.Errorf("first review holds development as an emergency (patients %v, hostiles unknown); the fixture roll is unusable, rerun as a fresh process", review.MedicalCare.Patients)
			}
		}
	}

	if err := sc.watch(ctx, journal, prepared, report); err != nil {
		if final, loadErr := journal.LoadRoutineReview(ctx); loadErr == nil {
			data, _ := json.Marshal(final)
			report["routine_review_at_failure"] = json.RawMessage(data)
		}
		return err
	}
	if err := na.AssertRoutineRunning(service.Get); err != nil {
		return err
	}

	// Independent native read after the service releases the game slot.
	journal.Close()
	service.Stop()
	h, err = reacquireSession()
	if err != nil {
		return err
	}
	if reuse != nil {
		// The service was stopped, not shut down, so its authority grant
		// lingers until the tick budget lapses; EndCase would retire the
		// game over it.
		if revoked, err := na.ReleaseAuthority(ctx, h, identity); err != nil {
			return err
		} else if revoked != nil {
			report["authority_released"] = revoked
		}
	}
	if _, err := h.Call(ctx, "pause-after", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	after, err := readUpkeep(ctx, h, identity, "upkeep-after")
	if err != nil {
		return err
	}
	report["upkeep_after"] = after
	if err := sc.verify(ctx, h, identity, prepared, report); err != nil {
		return err
	}
	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}

func callFixture(ctx context.Context, h *na.Harness, identity map[string]any, tool string, args map[string]any) (map[string]any, error) {
	prepared, err := h.Call(ctx, "prepare", tool, args)
	if err != nil {
		return nil, err
	}
	if success, _ := na.AsBool(prepared["success"]); !success {
		return nil, fmt.Errorf("%s refused: %#v", tool, prepared)
	}
	return prepared, nil
}

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

// ---- native reads --------------------------------------------------------

type itemRow struct {
	Definition string `json:"definition"`
	X          int    `json:"x"`
	Z          int    `json:"z"`
	Count      int64  `json:"count"`
	Roofed     bool   `json:"roofed"`
	InStorage  bool   `json:"inStorage"`
	Forbidden  bool   `json:"forbidden"`
	Medicine   bool   `json:"medicine"`
}

type structureRow struct {
	Definition string `json:"definition"`
	Home       bool   `json:"home"`
	HitPoints  int64  `json:"hitPoints"`
	Max        int64  `json:"maxHitPoints"`
}

type fireRow struct {
	Home bool    `json:"home"`
	Size float64 `json:"size"`
}

type upkeepCensus struct {
	Tick       int64                   `json:"tick"`
	Items      map[string]itemRow      `json:"items"`
	Structures map[string]structureRow `json:"structures"`
	Fires      map[string]fireRow      `json:"fires"`
	Filth      int                     `json:"filth"`
}

func readColonyFacts(ctx context.Context, h *na.Harness, identity map[string]any, label string) (map[string]any, error) {
	reply, err := h.Wire(ctx, label, "observations_read_colony_facts", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "planning": false, "page": map[string]any{"limit": 256},
	})
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	return observed, err
}

// readUpkeep decodes the typed upkeep census the review reads: loose items
// with their storage state, damageable structures, fires and filth.
func readUpkeep(ctx context.Context, h *na.Harness, identity map[string]any, label string) (upkeepCensus, error) {
	c := upkeepCensus{Items: map[string]itemRow{}, Structures: map[string]structureRow{}, Fires: map[string]fireRow{}}
	observed, err := readColonyFacts(ctx, h, identity, label)
	if err != nil {
		return c, err
	}
	context, _ := na.AsMap(observed["context"])
	c.Tick = int64(na.AsNumber(context["tick"]))
	section, _ := na.AsMap(observed["upkeep"])
	_, upkeep, err := na.Outcome(section, "observed")
	if err != nil {
		return c, fmt.Errorf("%s: upkeep facts unavailable: %w", label, err)
	}
	for _, raw := range na.AsSlice(upkeep["items"]) {
		row, _ := na.AsMap(raw)
		item, _ := na.AsMap(row["item"])
		position, _ := na.AsMap(item["position"])
		roofed, _ := na.AsBool(row["roofed"])
		inStorage, _ := na.AsBool(row["inStorage"])
		forbidden, _ := na.AsBool(row["forbidden"])
		medicine, _ := na.AsBool(row["medicine"])
		c.Items[na.AsString(item["id"])] = itemRow{Definition: na.AsString(item["defName"]), X: int(na.AsNumber(position["x"])), Z: int(na.AsNumber(position["z"])),
			Count: int64(na.AsNumber(row["count"])), Roofed: roofed, InStorage: inStorage, Forbidden: forbidden, Medicine: medicine}
	}
	for _, raw := range na.AsSlice(upkeep["structures"]) {
		row, _ := na.AsMap(raw)
		state, _ := na.AsMap(row["building"])
		building, _ := na.AsMap(state["building"])
		home, _ := na.AsBool(row["home"])
		c.Structures[na.AsString(building["id"])] = structureRow{Definition: na.AsString(building["defName"]), Home: home,
			HitPoints: int64(na.AsNumber(state["hitPoints"])), Max: int64(na.AsNumber(state["maxHitPoints"]))}
	}
	for _, raw := range na.AsSlice(upkeep["fires"]) {
		row, _ := na.AsMap(raw)
		fire, _ := na.AsMap(row["fire"])
		home, _ := na.AsBool(row["home"])
		c.Fires[na.AsString(fire["id"])] = fireRow{Home: home, Size: na.AsNumber(row["size"])}
	}
	c.Filth = len(na.AsSlice(upkeep["filth"]))
	return c, nil
}

// ---- journal helpers -----------------------------------------------------

// waitNeed polls until need's goal binding reports state (deficit or
// recovered) and returns the goal.
func waitNeed(ctx context.Context, journal *store.Store, need policy.GoalID, state domain.NeedState) (store.GoalState, error) {
	for {
		review, err := journal.LoadRoutineReview(ctx)
		if err != nil {
			return store.GoalState{}, err
		}
		for _, binding := range review.Goals {
			if binding.Need != need {
				continue
			}
			goal, err := journal.LoadGoal(ctx, binding.Goal)
			if err != nil && !errors.Is(err, store.ErrNotFound) {
				return store.GoalState{}, err
			}
			if err == nil && goal.Goal.Need == state {
				return goal, nil
			}
		}
		select {
		case <-ctx.Done():
			return store.GoalState{}, fmt.Errorf("%s never reported %s: %w", need, state, ctx.Err())
		case <-time.After(time.Second):
		}
	}
}

// followMethods waits for methods on need whose plan's single action
// satisfies accept, following the goal's method lineage across incidental
// (authority-discontinuity) cancellations until one plan completes. The
// controller's order supplements ordinary colonist work rather than replacing
// it: when the goal recovers on the native postcondition before the
// controller's plan completes (a colonist hauled or repaired it on their own
// priorities), the review invalidates the goal and cancels its never-dispatched
// plan; that counts as recovery too and is recorded as
// <label>_recovered_by=ordinary_work instead of <label>_completed_tick. Either
// way the scenario's verify step confirms the postcondition natively.
func followMethods(ctx context.Context, journal *store.Store, need policy.GoalID, label string, accept func(domain.Action) error, report na.Report) (store.PlanState, error) {
	return followMethodsExcluding(ctx, journal, need, label, map[domain.PlanID]bool{}, accept, report)
}

// followMethodsExcluding is followMethods over a caller-owned seen set, so a
// goal whose deficit needs two successive methods (a bed built, then that
// bed assigned) is followed method by method without revisiting the first.
func followMethodsExcluding(ctx context.Context, journal *store.Store, need policy.GoalID, label string, seen map[domain.PlanID]bool, accept func(domain.Action) error, report na.Report) (store.PlanState, error) {
	renewals, rejected := 0, 0
	recovered := func() (bool, error) {
		review, err := journal.LoadRoutineReview(ctx)
		if err != nil {
			return false, err
		}
		for _, binding := range review.Goals {
			if binding.Need != need {
				continue
			}
			goal, err := journal.LoadGoal(ctx, binding.Goal)
			if err != nil {
				if errors.Is(err, store.ErrNotFound) {
					continue
				}
				return false, err
			}
			if goal.Goal.Need == domain.NeedRecovered {
				return true, nil
			}
		}
		return false, nil
	}
	deadline := time.Now().Add(10 * time.Minute)
	for {
		var goalID domain.GoalID
		var method domain.GoalMethod
		for {
			if time.Now().After(deadline) {
				return store.PlanState{}, fmt.Errorf("%s method: no method within 10 minutes", label)
			}
			methodCtx, methodCancel := context.WithTimeout(ctx, 15*time.Second)
			var err error
			goalID, method, err = na.WaitGoalMethodExcluding(methodCtx, journal, need, seen)
			methodCancel()
			if err == nil {
				break
			}
			if ctx.Err() != nil {
				return store.PlanState{}, fmt.Errorf("%s method: %w", label, ctx.Err())
			}
			if done, rerr := recovered(); rerr != nil {
				return store.PlanState{}, rerr
			} else if done {
				report[label+"_recovered_by"] = "ordinary_work"
				return store.PlanState{}, nil
			}
		}
		seen[method.Plan] = true
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return store.PlanState{}, err
		}
		// A harvest acquisition designates one action per plant, so a plan
		// may carry several actions; every one must be acceptable.
		actions := plan.Spec.Actions()
		if len(actions) == 0 {
			return store.PlanState{}, fmt.Errorf("%s plan %s has no actions", label, method.Plan)
		}
		var rejectedErr error
		for _, action := range actions {
			if rejectedErr = accept(action); rejectedErr != nil {
				break
			}
		}
		if err := rejectedErr; err != nil {
			rejected++
			report[label+"_rejected_methods"] = rejected
			if rejected > 16 {
				return store.PlanState{}, fmt.Errorf("%s: %d methods without an acceptable action; last: %v", label, rejected, err)
			}
			continue
		}
		report[label+"_goal_id"] = string(goalID)
		report[label+"_method"] = string(method.Method)
		report[label+"_plan"] = string(method.Plan)
		// The plan is followed to a terminal stage, but a need that recovers
		// on its own while the plan is still undispatched ends the follow:
		// a recovered routine goal never authorizes that write again
		// (store.AuthorizeRoutinePlan), it merely keeps the plan for a
		// returning deficit, so no terminal stage is coming.
		state, incidental, undispatched, err := waitPlanOrRecovery(ctx, journal, method.Plan, recovered)
		if err != nil {
			return state, fmt.Errorf("%s plan %s: %w", label, method.Plan, err)
		}
		if undispatched {
			report[label+"_recovered_by"] = "ordinary_work"
			report[label+"_plan_undispatched"] = true
			return state, nil
		}
		if incidental {
			if done, rerr := recovered(); rerr != nil {
				return state, rerr
			} else if done {
				report[label+"_recovered_by"] = "ordinary_work"
				return state, nil
			}
			renewals++
			report[label+"_incidental_renewals"] = renewals
			if renewals > 12 {
				return state, fmt.Errorf("%s: %d incidental renewals without completion", label, renewals)
			}
			deadline = time.Now().Add(10 * time.Minute)
			continue
		}
		completed := int64(0)
		for _, progress := range state.Progress {
			completed = max(completed, int64(progress.View().Tick))
		}
		report[label+"_completed_tick"] = completed
		report[label+"_actions"] = len(actions)
		report[label+"_recovered_by"] = "controller_order"
		return state, nil
	}
}

// waitPlanOrRecovery waits up to ten minutes for plan to reach a terminal
// stage (na.WaitPlanTerminal), returning undispatched=true instead when the
// goal recovers while every action of the plan is still at attempt 0.
func waitPlanOrRecovery(ctx context.Context, journal *store.Store, planID domain.PlanID, recovered func() (bool, error)) (state store.PlanState, incidental, undispatched bool, err error) {
	doneCtx, doneCancel := context.WithTimeout(ctx, 10*time.Minute)
	defer doneCancel()
	type outcome struct {
		state      store.PlanState
		incidental bool
		err        error
	}
	done := make(chan outcome, 1)
	go func() {
		s, i, e := na.WaitPlanTerminal(doneCtx, journal, planID)
		done <- outcome{s, i, e}
	}()
	ticker := time.NewTicker(5 * time.Second)
	defer ticker.Stop()
	for {
		select {
		case o := <-done:
			return o.state, o.incidental, false, o.err
		case <-ticker.C:
			ok, rerr := recovered()
			if rerr != nil || !ok {
				continue
			}
			current, lerr := journal.LoadPlan(ctx, planID)
			if lerr != nil {
				continue
			}
			pending := len(current.Progress) > 0
			for _, progress := range current.Progress {
				v := progress.View()
				if v.Attempt != 0 || v.Stage != domain.Pending && v.Stage != domain.Prepared {
					pending = false
				}
			}
			if pending {
				doneCancel()
				<-done
				return current, false, true, nil
			}
		}
	}
}

// developmentRow returns the current review's development row for goal.
func developmentRow(ctx context.Context, journal *store.Store, goal policy.GoalID) (store.RoutineDevelopmentRow, bool, error) {
	review, err := journal.LoadRoutineReview(ctx)
	if err != nil {
		return store.RoutineDevelopmentRow{}, false, err
	}
	for _, row := range review.Development.Rows {
		if row.Goal == domain.GoalID(goal) {
			return row, true, nil
		}
	}
	return store.RoutineDevelopmentRow{}, false, nil
}

// ---- scattered -----------------------------------------------------------

func watchScattered(ctx context.Context, journal *store.Store, prepared map[string]any, report na.Report) error {
	medicine := na.AsString(prepared["medicine"])
	wall := na.AsString(prepared["wall"])
	deficitCtx, deficitCancel := context.WithTimeout(ctx, 4*time.Minute)
	defer deficitCancel()
	if _, err := waitNeed(deficitCtx, journal, policy.SecureSupplies, domain.NeedDeficit); err != nil {
		return err
	}
	if _, err := waitNeed(deficitCtx, journal, policy.MaintainEssentialRepairs, domain.NeedDeficit); err != nil {
		return err
	}
	// The medicine sorts first among vulnerable items, so the first haul
	// method must target it; the repair method must target the fixture wall.
	if _, err := followMethods(ctx, journal, policy.SecureSupplies, "haul", func(a domain.Action) error {
		haul, ok := a.Haul()
		if !ok {
			return fmt.Errorf("not a haul: %v", a.Kind())
		}
		if haul.Thing() != medicine {
			return fmt.Errorf("haul targets %s, not the fixture medicine %s", haul.Thing(), medicine)
		}
		return nil
	}, report); err != nil {
		return err
	}
	if _, err := followMethods(ctx, journal, policy.MaintainEssentialRepairs, "repair", func(a domain.Action) error {
		repair, ok := a.Repair()
		if !ok {
			return fmt.Errorf("not a repair: %v", a.Kind())
		}
		if repair.Structure() != wall {
			return fmt.Errorf("repair targets %s, not the fixture wall %s", repair.Structure(), wall)
		}
		return nil
	}, report); err != nil {
		return err
	}
	recoverCtx, recoverCancel := context.WithTimeout(ctx, 6*time.Minute)
	defer recoverCancel()
	supplies, err := waitNeed(recoverCtx, journal, policy.SecureSupplies, domain.NeedRecovered)
	if err != nil {
		return err
	}
	report["supplies_recovered_tick"] = int64(supplies.Goal.Tick)
	repairs, err := waitNeed(recoverCtx, journal, policy.MaintainEssentialRepairs, domain.NeedRecovered)
	if err != nil {
		return err
	}
	report["repairs_recovered_tick"] = int64(repairs.Goal.Tick)
	// Outdoor dirt is ordinary colonist work, never a controller order.
	review, err := journal.LoadRoutineReview(ctx)
	if err != nil {
		return err
	}
	for _, binding := range review.Goals {
		if binding.Need != policy.MaintainCleanFacilities {
			continue
		}
		goal, err := journal.LoadGoal(ctx, binding.Goal)
		if err == nil && len(goal.Methods) > 0 {
			return fmt.Errorf("MaintainCleanFacilities issued %d methods for outdoor dirt", len(goal.Methods))
		}
	}
	report["dirty_rooms_latched"] = len(review.Latches.Upkeep.DirtyRooms)
	return nil
}

func verifyScattered(ctx context.Context, h *na.Harness, identity, prepared map[string]any, report na.Report) error {
	after := report["upkeep_after"].(upkeepCensus)
	medicine := na.AsString(prepared["medicine"])
	wall := na.AsString(prepared["wall"])
	item, ok := after.Items[medicine]
	if !ok {
		return fmt.Errorf("medicine %s missing from the upkeep census after recovery", medicine)
	}
	if !item.Roofed || !item.InStorage {
		return fmt.Errorf("medicine %s is not in covered storage natively: %+v", medicine, item)
	}
	storage, _ := na.AsMap(prepared["storage"])
	sx, sz := int(na.AsNumber(storage["x"])), int(na.AsNumber(storage["z"]))
	if item.X < sx || item.X > sx+1 || item.Z < sz || item.Z > sz+1 {
		return fmt.Errorf("medicine %s rests at (%d,%d), outside the fixture stockpile at (%d,%d)-(%d,%d)", medicine, item.X, item.Z, sx, sz, sx+1, sz+1)
	}
	structure, ok := after.Structures[wall]
	if !ok {
		return fmt.Errorf("wall %s missing from the upkeep census after recovery", wall)
	}
	if structure.HitPoints != structure.Max {
		return fmt.Errorf("wall %s stands at %d/%d natively", wall, structure.HitPoints, structure.Max)
	}
	return nil
}

// ---- storage-missing -----------------------------------------------------

func watchStorageMissing(ctx context.Context, journal *store.Store, prepared map[string]any, report na.Report) error {
	medicine := na.AsString(prepared["medicine"])
	deficitCtx, deficitCancel := context.WithTimeout(ctx, 4*time.Minute)
	defer deficitCancel()
	if _, err := waitNeed(deficitCtx, journal, policy.SecureSupplies, domain.NeedDeficit); err != nil {
		return err
	}
	// Ordinary hauls are refused natively (no storage accepts the medicine)
	// until the covered-storage fallback proposes a filtered stockpile.
	seen := map[domain.PlanID]bool{}
	refused := 0
	for {
		methodCtx, methodCancel := context.WithTimeout(ctx, 12*time.Minute)
		_, method, err := na.WaitGoalMethodExcluding(methodCtx, journal, policy.SecureSupplies, seen)
		methodCancel()
		if err != nil {
			return fmt.Errorf("secure-supplies method: %w", err)
		}
		seen[method.Plan] = true
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return err
		}
		actions := plan.Spec.Actions()
		if len(actions) != 1 {
			return fmt.Errorf("plan %s has %d actions", method.Plan, len(actions))
		}
		if zone, ok := actions[0].ZoneCreate(); ok {
			report["zone_method"] = string(method.Method)
			report["zone_plan"] = string(method.Plan)
			report["zone_cells"] = zone.Cells()
			doneCtx, doneCancel := context.WithTimeout(ctx, 6*time.Minute)
			state, incidental, err := na.WaitPlanTerminal(doneCtx, journal, method.Plan)
			doneCancel()
			if err != nil {
				return fmt.Errorf("zone plan %s: %w", method.Plan, err)
			}
			if incidental {
				continue
			}
			report["zone_completed_tick"] = int64(state.Progress[0].View().Tick)
			break
		}
		haul, ok := actions[0].Haul()
		if !ok {
			return fmt.Errorf("unexpected %s action before the zone fallback", actions[0].Kind())
		}
		if haul.Thing() != medicine {
			return fmt.Errorf("haul targets %s, not the fixture medicine %s", haul.Thing(), medicine)
		}
		doneCtx, doneCancel := context.WithTimeout(ctx, 6*time.Minute)
		state, incidental, err := na.WaitPlanTerminal(doneCtx, journal, method.Plan)
		doneCancel()
		if incidental {
			continue
		}
		if err == nil {
			for _, p := range state.Progress {
				if p.View().Stage == domain.Completed {
					return fmt.Errorf("haul %s completed although no storage accepts the medicine", method.Plan)
				}
			}
		}
		refused++
		report["hauls_refused_before_zone"] = refused
		if refused > 12 {
			return fmt.Errorf("%d refused hauls without a storage fallback", refused)
		}
	}
	if _, err := followMethods(ctx, journal, policy.SecureSupplies, "haul", func(a domain.Action) error {
		haul, ok := a.Haul()
		if !ok {
			return fmt.Errorf("not a haul: %v", a.Kind())
		}
		if haul.Thing() != medicine {
			return fmt.Errorf("haul targets %s, not the fixture medicine %s", haul.Thing(), medicine)
		}
		return nil
	}, report); err != nil {
		return err
	}
	recoverCtx, recoverCancel := context.WithTimeout(ctx, 6*time.Minute)
	defer recoverCancel()
	supplies, err := waitNeed(recoverCtx, journal, policy.SecureSupplies, domain.NeedRecovered)
	if err != nil {
		return err
	}
	report["supplies_recovered_tick"] = int64(supplies.Goal.Tick)
	return nil
}

func verifyStorageMissing(ctx context.Context, h *na.Harness, identity, prepared map[string]any, report na.Report) error {
	after := report["upkeep_after"].(upkeepCensus)
	medicine := na.AsString(prepared["medicine"])
	item, ok := after.Items[medicine]
	if !ok {
		return fmt.Errorf("medicine %s missing from the upkeep census after recovery", medicine)
	}
	if !item.Roofed || !item.InStorage {
		return fmt.Errorf("medicine %s is not in covered storage natively: %+v", medicine, item)
	}
	zones, err := readStockpiles(ctx, h, identity, item.X, item.Z)
	if err != nil {
		return err
	}
	report["stockpiles_after"] = zones
	for _, z := range zones {
		for _, c := range z.cells {
			if c[0] == item.X && c[1] == item.Z {
				report["medicine_stockpile"] = z.id
				return nil
			}
		}
	}
	return fmt.Errorf("medicine at (%d,%d) is not inside any native stockpile: %+v", item.X, item.Z, zones)
}

type stockpile struct {
	id    string
	cells [][2]int
}

func (s stockpile) MarshalJSON() ([]byte, error) {
	return json.Marshal(map[string]any{"id": s.id, "cells": s.cells})
}

// readStockpiles lists the stockpile zones covering one cell: the tool
// is bounded (page limit 16), so it is asked for that cell's region only.
func readStockpiles(ctx context.Context, h *na.Harness, identity map[string]any, x, z int) ([]stockpile, error) {
	cell := map[string]any{"x": x, "z": z}
	reply, err := h.Wire(ctx, "zones-after", "observations_list_zones", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "includeCells": true,
		"region": map[string]any{"minimum": cell, "maximum": cell}, "page": map[string]any{"limit": 16},
	})
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, err
	}
	var zones []stockpile
	for _, raw := range na.AsSlice(observed["zones"]) {
		row, _ := na.AsMap(raw)
		if !strings.Contains(strings.ToLower(na.AsString(row["kind"])+na.AsString(row["type"])), "stockpile") {
			continue
		}
		z := stockpile{id: na.AsString(row["id"])}
		cells := na.AsSlice(row["listedCells"])
		if len(cells) == 0 {
			cells = na.AsSlice(row["gridCells"])
		}
		for _, rawCell := range cells {
			cell, _ := na.AsMap(rawCell)
			z.cells = append(z.cells, [2]int{int(na.AsNumber(cell["x"])), int(na.AsNumber(cell["z"]))})
		}
		zones = append(zones, z)
	}
	return zones, nil
}

// ---- blocked -------------------------------------------------------------

func watchBlocked(ctx context.Context, journal *store.Store, prepared map[string]any, report na.Report) error {
	deficitCtx, deficitCancel := context.WithTimeout(ctx, 4*time.Minute)
	defer deficitCancel()
	if _, err := waitNeed(deficitCtx, journal, policy.SecureSupplies, domain.NeedDeficit); err != nil {
		return err
	}
	if _, err := waitNeed(deficitCtx, journal, policy.MaintainEssentialRepairs, domain.NeedDeficit); err != nil {
		return err
	}
	// Hold the window open long enough for several review cycles and any
	// dispatch attempts; nothing may complete and the deficits must stay
	// visible with their attempts recorded rather than silently dropped.
	window := time.After(6 * time.Minute)
	completed := map[string]int{}
	attempts := map[string]int{}
	for {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-window:
			review, err := journal.LoadRoutineReview(ctx)
			if err != nil {
				return err
			}
			for _, need := range []policy.GoalID{policy.SecureSupplies, policy.MaintainEssentialRepairs} {
				goal, err := waitNeed(ctx, journal, need, domain.NeedDeficit)
				if err != nil {
					return fmt.Errorf("%s no longer in deficit although its targets are unreachable", need)
				}
				report[string(need)+"_status"] = string(goal.Goal.Status)
				row, found, err := developmentRow(ctx, journal, need)
				if err != nil {
					return err
				}
				if !found {
					return fmt.Errorf("%s has no development row in review %d", need, review.Revision)
				}
				report[string(need)+"_development_reason"] = string(row.Reason)
				report[string(need)+"_attempts"] = attempts[string(need)]
			}
			report["completed_plans"] = completed
			return nil
		case <-time.After(5 * time.Second):
		}
		review, err := journal.LoadRoutineReview(ctx)
		if err != nil {
			return err
		}
		for _, binding := range review.Goals {
			if binding.Need != policy.SecureSupplies && binding.Need != policy.MaintainEssentialRepairs {
				continue
			}
			goal, err := journal.LoadGoal(ctx, binding.Goal)
			if err != nil {
				continue
			}
			methods := goal.Methods
			if history, err := journal.LoadGoalMethods(ctx, binding.Goal, goal.Goal.Epoch); err == nil && len(history) > len(methods) {
				methods = history
			}
			attempts[string(binding.Need)] = len(methods)
			for _, method := range methods {
				plan, err := journal.LoadPlan(ctx, method.Plan)
				if err != nil {
					continue
				}
				for _, p := range plan.Progress {
					if p.View().Stage == domain.Completed {
						completed[string(method.Plan)]++
						return fmt.Errorf("%s plan %s completed although its target is walled off from every worker", binding.Need, method.Plan)
					}
				}
			}
		}
	}
}

func verifyBlocked(ctx context.Context, h *na.Harness, identity, prepared map[string]any, report na.Report) error {
	before := report["upkeep_before"].(upkeepCensus)
	after := report["upkeep_after"].(upkeepCensus)
	medicine := na.AsString(prepared["medicine"])
	wall := na.AsString(prepared["wall"])
	was, ok := before.Items[medicine]
	if !ok {
		return fmt.Errorf("medicine %s missing from the upkeep census before the service ran", medicine)
	}
	item, ok := after.Items[medicine]
	if !ok {
		return fmt.Errorf("medicine %s vanished from the upkeep census", medicine)
	}
	if item.InStorage || item.X != was.X || item.Z != was.Z {
		return fmt.Errorf("medicine %s moved from (%d,%d) to %+v although no worker may reach it", medicine, was.X, was.Z, item)
	}
	structure, ok := after.Structures[wall]
	if !ok {
		return fmt.Errorf("wall %s missing from the upkeep census", wall)
	}
	if structure.HitPoints >= structure.Max {
		return fmt.Errorf("wall %s was repaired to %d/%d although no worker may reach it", wall, structure.HitPoints, structure.Max)
	}
	return nil
}

// ---- fire ----------------------------------------------------------------

func watchFire(ctx context.Context, journal *store.Store, prepared map[string]any, report na.Report) error {
	deficitCtx, deficitCancel := context.WithTimeout(ctx, 3*time.Minute)
	defer deficitCancel()
	goal, err := waitNeed(deficitCtx, journal, policy.MaintainFireSafety, domain.NeedDeficit)
	if err != nil {
		return err
	}
	report["fire_goal_priority"] = goal.Goal.Priority
	if goal.Goal.Priority > 1 {
		return fmt.Errorf("fire goal ranked at priority %d, not an emergency", goal.Goal.Priority)
	}
	review, err := journal.LoadRoutineReview(ctx)
	if err != nil {
		return err
	}
	report["fire_latched_revision"] = review.Revision
	report["fire_latched_tick"] = int64(review.Tick)
	if !review.Latches.Upkeep.Fire {
		return fmt.Errorf("fire latch not set in review %d while the goal is in deficit", review.Revision)
	}
	reasons := map[string]string{}
	deferred := 0
	for _, row := range review.Development.Rows {
		reasons[string(row.Goal)] = string(row.Reason)
		if row.Reason == policy.DevelopmentEmergency {
			deferred++
		}
		if row.Selected {
			return fmt.Errorf("development row %s selected during a fire emergency", row.Goal)
		}
	}
	report["development_reasons_during_fire"] = reasons
	if len(review.Development.Rows) > 0 && deferred == 0 {
		return fmt.Errorf("no development row deferred as emergency during the fire: %v", reasons)
	}
	recoverCtx, recoverCancel := context.WithTimeout(ctx, 8*time.Minute)
	defer recoverCancel()
	recovered, err := waitNeed(recoverCtx, journal, policy.MaintainFireSafety, domain.NeedRecovered)
	if err != nil {
		return err
	}
	report["fire_recovered_tick"] = int64(recovered.Goal.Tick)
	final, err := journal.LoadRoutineReview(ctx)
	if err != nil {
		return err
	}
	if final.Latches.Upkeep.Fire {
		return fmt.Errorf("fire latch still set in review %d after recovery", final.Revision)
	}
	return nil
}

func verifyFire(ctx context.Context, h *na.Harness, identity, prepared map[string]any, report na.Report) error {
	before := report["upkeep_before"].(upkeepCensus)
	after := report["upkeep_after"].(upkeepCensus)
	fire := na.AsString(prepared["fire"])
	if row, ok := before.Fires[fire]; !ok || !row.Home {
		return fmt.Errorf("fixture fire %s not observed at home before the service ran: %+v", fire, before.Fires)
	}
	for id, row := range after.Fires {
		if row.Home {
			return fmt.Errorf("home fire %s still burns natively (size %.2f)", id, row.Size)
		}
	}
	return nil
}

// ---- medicine ------------------------------------------------------------

func watchMedicine(ctx context.Context, journal *store.Store, prepared map[string]any, report na.Report) error {
	deficitCtx, deficitCancel := context.WithTimeout(ctx, 4*time.Minute)
	defer deficitCancel()
	if _, err := waitNeed(deficitCtx, journal, policy.MaintainMedicalReserves, domain.NeedDeficit); err != nil {
		return err
	}
	// The method is an ordinary resource acquisition of the native
	// herbal-medicine definition; follow it to completion, then require the
	// observed reserve rather than the receipt.
	if _, err := followMethods(ctx, journal, policy.MaintainMedicalReserves, "replenish", func(a domain.Action) error {
		switch a.Kind() {
		case domain.AcquisitionAction, domain.ProductionBillAction, domain.MineAcquisitionAction:
			return nil
		}
		return fmt.Errorf("unexpected %s action", a.Kind())
	}, report); err != nil {
		return err
	}
	recoverCtx, recoverCancel := context.WithTimeout(ctx, 12*time.Minute)
	defer recoverCancel()
	goal, err := waitNeed(recoverCtx, journal, policy.MaintainMedicalReserves, domain.NeedRecovered)
	if err != nil {
		return err
	}
	report["medicine_recovered_tick"] = int64(goal.Goal.Tick)
	return nil
}

func verifyMedicine(ctx context.Context, h *na.Harness, identity, prepared map[string]any, report na.Report) error {
	after := report["upkeep_after"].(upkeepCensus)
	units := int64(0)
	for _, item := range after.Items {
		if item.Medicine && !item.Forbidden {
			units += item.Count
		}
	}
	colonists, err := countColonists(ctx, h, identity)
	if err != nil {
		return err
	}
	reserve := policy.DefaultMedicalReservePolicy()
	report["medicine_units_after"] = units
	report["colonists"] = colonists
	if units < reserve.TargetPerColonist*int64(colonists) {
		return fmt.Errorf("%d medicine units for %d colonists is under the recovery reserve of %d each", units, colonists, reserve.TargetPerColonist)
	}
	return nil
}

func countColonists(ctx context.Context, h *na.Harness, identity map[string]any) (int, error) {
	observed, err := readColonyFacts(ctx, h, identity, "colonists-after")
	if err != nil {
		return 0, err
	}
	return int(na.AsNumber(observed["colonistCount"])), nil
}

// ---- feed ----------------------------------------------------------------

func watchFeed(ctx context.Context, journal *store.Store, prepared map[string]any, report na.Report) error {
	deficitCtx, deficitCancel := context.WithTimeout(ctx, 4*time.Minute)
	defer deficitCancel()
	if _, err := waitNeed(deficitCtx, journal, policy.MaintainAnimalFeed, domain.NeedDeficit); err != nil {
		return err
	}
	if _, err := followMethods(ctx, journal, policy.MaintainAnimalFeed, "feed", func(a domain.Action) error {
		switch a.Kind() {
		case domain.AcquisitionAction, domain.ProductionBillAction, domain.MineAcquisitionAction:
			return nil
		}
		return fmt.Errorf("unexpected %s action", a.Kind())
	}, report); err != nil {
		return err
	}
	recoverCtx, recoverCancel := context.WithTimeout(ctx, 12*time.Minute)
	defer recoverCancel()
	goal, err := waitNeed(recoverCtx, journal, policy.MaintainAnimalFeed, domain.NeedRecovered)
	if err != nil {
		return err
	}
	report["feed_recovered_tick"] = int64(goal.Goal.Tick)
	return nil
}

func verifyFeed(ctx context.Context, h *na.Harness, identity, prepared map[string]any, report na.Report) error {
	pet := na.AsString(prepared["pet"])
	observed, err := readColonyFacts(ctx, h, identity, "animals-after")
	if err != nil {
		return err
	}
	section, _ := na.AsMap(observed["upkeep"])
	_, upkeep, err := na.Outcome(section, "observed")
	if err != nil {
		return err
	}
	for _, raw := range na.AsSlice(upkeep["animals"]) {
		row, _ := na.AsMap(raw)
		pawn, _ := na.AsMap(row["pawn"])
		ref, _ := na.AsMap(pawn["pawn"])
		if na.AsString(ref["id"]) != pet {
			continue
		}
		feed := na.AsSlice(row["reachableStoredFeed"])
		report["pet_reachable_feed_rows"] = len(feed)
		nutrition := 0.0
		for _, rawStock := range feed {
			stock, _ := na.AsMap(rawStock)
			nutrition += na.AsNumber(stock["nutrition"])
		}
		report["pet_reachable_nutrition"] = nutrition
		if len(feed) == 0 || nutrition <= 0 {
			return fmt.Errorf("pet %s has no reachable stored feed natively after recovery", pet)
		}
		return nil
	}
	return fmt.Errorf("pet %s missing from the native animal feed census", pet)
}

// ---- sleeping ------------------------------------------------------------

func watchSleeping(ctx context.Context, journal *store.Store, prepared map[string]any, report na.Report) error {
	deficitCtx, deficitCancel := context.WithTimeout(ctx, 4*time.Minute)
	defer deficitCancel()
	if _, err := waitNeed(deficitCtx, journal, policy.MaintainSleeping, domain.NeedDeficit); err != nil {
		return err
	}
	// The fixture leaves no vacant suitable bed, so the first method builds
	// one (a Building action). Ownership then comes either from the
	// controller's AssignBed (a bed_assign action, followed as its own
	// method) or from the colonist claiming the new bed on their own; the
	// goal recovers only on observed sleep in an owned suitable bed, which
	// waitNeed below confirms and verifySleeping checks natively.
	seen := map[domain.PlanID]bool{}
	kinds := []string{}
	if _, err := followMethodsExcluding(ctx, journal, policy.MaintainSleeping, "bed", seen, func(a domain.Action) error {
		if _, ok := a.Building(); ok {
			kinds = append(kinds, string(a.Kind()))
			return nil
		}
		return fmt.Errorf("unexpected %s action before a bed was built", a.Kind())
	}, report); err != nil {
		return err
	}
	if report["bed_recovered_by"] != "ordinary_work" {
		if _, err := followMethodsExcluding(ctx, journal, policy.MaintainSleeping, "assign", seen, func(a domain.Action) error {
			if _, ok := a.BedAssign(); ok {
				kinds = append(kinds, string(a.Kind()))
				return nil
			}
			return fmt.Errorf("unexpected %s action after the bed was built", a.Kind())
		}, report); err != nil {
			return err
		}
	}
	report["sleeping_action_kinds"] = kinds
	recoverCtx, recoverCancel := context.WithTimeout(ctx, 15*time.Minute)
	defer recoverCancel()
	goal, err := waitNeed(recoverCtx, journal, policy.MaintainSleeping, domain.NeedRecovered)
	if err != nil {
		return err
	}
	report["sleeping_recovered_tick"] = int64(goal.Goal.Tick)
	return nil
}

func verifySleeping(ctx context.Context, h *na.Harness, identity, prepared map[string]any, report na.Report) error {
	observed, err := readColonyFacts(ctx, h, identity, "beds-after")
	if err != nil {
		return err
	}
	section, _ := na.AsMap(observed["upkeep"])
	_, upkeep, err := na.Outcome(section, "observed")
	if err != nil {
		return err
	}
	beds := na.AsSlice(upkeep["beds"])
	report["beds_after"] = len(beds)
	// Recovery needs an owned humanlike bed that is a real bed (a sleeping
	// spot never satisfies ReviewSleeping) under a roof.
	owned, suitable := 0, 0
	for _, raw := range beds {
		row, _ := na.AsMap(raw)
		if len(na.AsSlice(row["owners"])) == 0 {
			continue
		}
		owned++
		ref, _ := na.AsMap(row["bed"])
		if definition, _ := ref["defName"].(string); definition != "SleepingSpot" && row["roofed"] == true && row["humanlike"] == true {
			suitable++
		}
	}
	report["owned_beds_after"] = owned
	report["owned_suitable_beds_after"] = suitable
	colonists := int(na.AsNumber(observed["colonistCount"]))
	report["colonists"] = colonists
	if colonists == 0 || suitable < colonists {
		return fmt.Errorf("%d of %d colonists own a roofed bed after MaintainSleeping recovered", suitable, colonists)
	}
	return nil
}

// ---- cold ----------------------------------------------------------------

func prepareCold(ctx context.Context, h *na.Harness, identity map[string]any, report na.Report) (map[string]any, error) {
	room, err := callFixture(ctx, h, identity, "test/routine_sleeping_prepare", map[string]any{"outdoorSite": false})
	if err != nil {
		return nil, err
	}
	report["room"] = room
	center, _ := na.AsMap(room["center"])
	prepared, err := h.Call(ctx, "prepare-temperature", "test/routine_temperature_prepare", map[string]any{
		"x": int(na.AsNumber(center["x"])), "z": int(na.AsNumber(center["z"])), "hot": false,
	})
	if err != nil {
		if strings.Contains(err.Error(), "outdoor temperature") {
			return nil, fmt.Errorf("rolled map is not cold enough for the campfire fixture; rerun as a fresh process: %w", err)
		}
		return nil, err
	}
	if success, _ := na.AsBool(prepared["success"]); !success {
		return nil, fmt.Errorf("routine_temperature_prepare refused: %#v", prepared)
	}
	return prepared, nil
}

func watchCold(ctx context.Context, journal *store.Store, prepared map[string]any, report na.Report) error {
	deficitCtx, deficitCancel := context.WithTimeout(ctx, 4*time.Minute)
	defer deficitCancel()
	if _, err := waitNeed(deficitCtx, journal, policy.EnsureTemperatureSafety, domain.NeedDeficit); err != nil {
		return err
	}
	definition := na.AsString(prepared["definition"])
	if _, err := followMethods(ctx, journal, policy.EnsureTemperatureSafety, "heat", func(a domain.Action) error {
		b, ok := a.Building()
		if !ok {
			return fmt.Errorf("unexpected %s action", a.Kind())
		}
		if b.Definition() != definition {
			return fmt.Errorf("placed %s, expected the fixture's %s", b.Definition(), definition)
		}
		return nil
	}, report); err != nil {
		return err
	}
	recoverCtx, recoverCancel := context.WithTimeout(ctx, 12*time.Minute)
	defer recoverCancel()
	goal, err := waitNeed(recoverCtx, journal, policy.EnsureTemperatureSafety, domain.NeedRecovered)
	if err != nil {
		return err
	}
	report["temperature_recovered_tick"] = int64(goal.Goal.Tick)
	return nil
}

func verifyCold(ctx context.Context, h *na.Harness, identity, prepared map[string]any, report na.Report) error {
	observed, err := readColonyFacts(ctx, h, identity, "temperature-after")
	if err != nil {
		return err
	}
	minC, hasMin := observed["sleepingTemperatureMinC"]
	report["sleeping_temperature_min_c"] = minC
	report["outdoor_temperature_c"] = observed["outdoorTemperatureC"]
	if !hasMin {
		return errors.New("native sleeping temperature unknown after recovery")
	}
	section, _ := na.AsMap(observed["upkeep"])
	_, upkeep, err := na.Outcome(section, "observed")
	if err != nil {
		return err
	}
	var ids []string
	for _, raw := range na.AsSlice(upkeep["beds"]) {
		row, _ := na.AsMap(raw)
		bed, _ := na.AsMap(row["bed"])
		ids = append(ids, na.AsString(bed["id"]))
	}
	sort.Strings(ids)
	report["beds_after"] = ids
	if na.AsNumber(minC) <= na.AsNumber(observed["outdoorTemperatureC"]) {
		return fmt.Errorf("sleeping minimum %.1f C is no warmer than outdoors %.1f C", na.AsNumber(minC), na.AsNumber(observed["outdoorTemperatureC"]))
	}
	return nil
}

var _ = sustainedfood.SampleGoal
