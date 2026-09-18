// Command routinehaulaccept exercises the RoutineHaulPlanner/MaintainStorage
// vertical (G01.07b 05.2) end to end against a live game and a live
// rimgovernor Go player-control service: a worker is assigned and hauls a
// real ordinary (non-decaying) item into shared storage, the stored outcome
// is observed via a native read, a renewed deficit (a second item) is picked
// up without a duplicate or conflicting order, and a player-revoked Hauling
// priority interrupts dispatch without RoutineHaulPlanner overriding player
// intent or double-issuing. Uses a private disposable fixture
// (test/storage_haul_prepare, test/storage_haul_control) since native random
// colony generation cannot reliably produce a MaintainStorage deficit (an
// ordinary item outside legal storage) with a deterministic single eligible
// hauler; test/guarded_construction_prepare supplies the one player-submitted
// building plan MaintainStorage's own arbitration capacity slot requires to
// be occupied by "the accepted player project," matching
// policy.RankDevelopment/AuditDevelopment's own accounting.
//
// This binary launches its own GABS-backed bridge session (like every other
// nativeaccept command) to prepare the fixture and take independent native
// reads, and separately launches a prebuilt rimgovernor "serve" binary
// against the *same* GABS configuration/game so its own routine reviewer and
// haul planner/executor drive the actual native dispatch under test -- the
// planner and its dispatch are Go-owned production logic
// (buildingruntime.RoutineHaulPlanner), not something a bridge fixture can
// exercise directly.
//
// Only one GABP (game-side) client can be connected to the running game at a
// time, so this harness's own bridge session and the service subprocess's
// own bridge session are used SEQUENTIALLY, never concurrently: the harness
// prepares the fixture, then closes its session (without calling games_stop,
// which would actually terminate the game -- Client.Close only tears down
// this GABS subprocess's own MCP session, confirmed via bridge.Client's
// implementation) to free the slot before the service starts and connects.
// Once the service has completed dispatch and is stopped, a fresh harness
// session is reopened (games_start is idempotent) to take the final
// independent native read of the stored outcome. games_stop is called
// exactly once, from that last session, at the very end.
package main

import (
	"bufio"
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-routine-haul-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	rimgovernorBinary := flag.String("rimgovernor", "", "absolute path to a prebuilt rimgovernor binary (go build ./go/cmd/rimgovernor)")
	timeout := flag.Duration("timeout", 25*time.Minute, "overall run timeout")
	flightRecorder := flag.Bool("flight-recorder", false, "record the service's native timeline (flight.jsonl) and summarize its phases (reads/step, cache and parent hits) into the report")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *rimgovernorBinary == "" {
		fmt.Fprintln(os.Stderr, "-rimgovernor is required (absolute path to a prebuilt rimgovernor binary)")
		os.Exit(2)
	}
	if !filepath.IsAbs(*rimgovernorBinary) {
		fmt.Fprintln(os.Stderr, "-rimgovernor must be an absolute path")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-routine-haul-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	entries, _ := os.ReadDir(*output)
	if len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport("Native RoutineHaulPlanner/MaintainStorage vertical: a worker hauls a real "+
		"ordinary item into shared storage under the live Go routine reviewer/planner/executor, the stored "+
		"outcome is confirmed by an independent native read, a renewed deficit is picked up without a "+
		"duplicate order, and a player-revoked Hauling priority interrupts dispatch without the planner "+
		"overriding player intent or double-issuing.", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, !*rendered, *rimgovernorBinary, *flightRecorder, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	na.ReportPhases(report, *output, *flightRecorder)
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID string, headless bool, rimgovernorBinary string, flightRecorder bool, report na.Report) error {
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
	binarySHA, err := sha256File(rimgovernorBinary)
	if err != nil {
		return fmt.Errorf("hash rimgovernor binary: %w", err)
	}
	report["rimgovernor_binary"] = map[string]string{"path": rimgovernorBinary, "sha256": binarySHA}

	// Only one GABP (game-side) client can be connected at a time -- a second
	// GABS process's games_connect to an already-connected game is refused.
	// So this harness's own bridge session and the service subprocess's own
	// bridge session must be used SEQUENTIALLY, never concurrently: this
	// first session does fixture prep and "before" native reads, then is
	// released (without games_stop, which would actually terminate the game)
	// to free the slot for the service, and reattached afterwards for the
	// "after" native reads once the service has released the slot again.
	// held.Close ends the hold once, after the deferred stopService.
	held, err := na.OpenGame(ctx, cfg)
	if err != nil {
		return err
	}
	defer held.Close(report)
	h := na.NewHarness(held.Client, output)

	if _, err := na.StartDebugGame(ctx, h, nil, na.QuietRequired); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}

	// A fresh debug-started game leaves the initial faction/settlement naming
	// dialog open; RankDevelopment (go/internal/policy/development.go) treats
	// ConfirmColonyNames (priority 0) as a global "emergency" that blocks
	// every other goal -- including MaintainStorage -- from ever being
	// Selected until it is resolved. A real player would dismiss this dialog
	// within seconds of a new game; do the same here, from the harness's own
	// GABP session, before handing the sole slot to the service.
	facts, err := h.Call(ctx, "colony-facts", "home/colony_facts", map[string]any{})
	if err != nil {
		return err
	}
	if naming, ok := na.AsMap(facts["colonyNaming"]); ok && naming != nil {
		confirmed, err := h.Call(ctx, "confirm-colony-names", "home/confirm_colony_names", map[string]any{
			"windowId":       int(na.AsNumber(naming["windowId"])),
			"factionName":    na.AsString(naming["factionName"]),
			"settlementName": na.AsString(naming["settlementName"]),
			"dryRun":         false,
		})
		if err != nil {
			return err
		}
		if success, _ := na.AsBool(confirmed["success"]); !success {
			return fmt.Errorf("confirm_colony_names refused: %#v", confirmed)
		}
		report["confirmed_colony_names"] = confirmed
	} else {
		report["confirmed_colony_names"] = "no pending naming dialog"
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

	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	report["discovery"] = names
	for _, want := range []string{"test/storage_haul_prepare", "test/storage_haul_control", "test/guarded_construction_prepare"} {
		if !na.Contains(names, want) {
			return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture StorageHaulFixture -Fixture GuardedConstructionFixture", want)
		}
	}

	matchesIdentity := func(v map[string]any) bool {
		return na.AsString(v["colonyId"]) == na.AsString(identity["colonyId"]) &&
			na.AsString(v["loadToken"]) == na.AsString(identity["loadToken"]) &&
			na.AsNumber(v["mapId"]) == na.AsNumber(identity["mapId"])
	}

	prepared, err := h.Call(ctx, "prepare-storage", "test/storage_haul_prepare", map[string]any{"itemCount": 2})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(prepared["success"]); !success || !matchesIdentity(prepared) {
		return fmt.Errorf("storage_haul_prepare refused or identity mismatch: %#v", prepared)
	}
	report["prepared_storage"] = prepared
	haulerID := na.AsString(prepared["haulerId"])
	var itemIDs []string
	for _, raw := range na.AsSlice(prepared["itemIds"]) {
		itemIDs = append(itemIDs, fmt.Sprint(raw))
	}
	storageCell, _ := na.AsMap(prepared["storageCell"])
	storageX, storageZ := int(na.AsNumber(storageCell["x"])), int(na.AsNumber(storageCell["z"]))
	if haulerID == "" || len(itemIDs) != 2 {
		return fmt.Errorf("storage_haul_prepare: unexpected fixture identifiers: %#v", prepared)
	}
	// Confirm, from the fixture's own spawn-time coordinates, that each item
	// genuinely starts outside storage -- a pre-condition of this being real
	// acceptance evidence, not a fixture that already put it there. Taken
	// while the harness is still connected, before it hands the sole GABP
	// slot to the service.
	for _, raw := range na.AsSlice(prepared["items"]) {
		item, _ := na.AsMap(raw)
		x, z := int(na.AsNumber(item["x"])), int(na.AsNumber(item["z"]))
		if x == storageX && z == storageZ {
			return fmt.Errorf("item %v already at the storage cell before any haul: %#v", item["id"], item)
		}
	}

	construction, err := h.Call(ctx, "prepare-construction", "test/guarded_construction_prepare", map[string]any{"siteCount": 1})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(construction["success"]); !success || !matchesIdentity(construction) {
		return fmt.Errorf("guarded_construction_prepare refused or identity mismatch: %#v", construction)
	}
	report["prepared_construction"] = construction
	sites := na.AsSlice(construction["sites"])
	if len(sites) != 1 {
		return fmt.Errorf("guarded_construction_prepare: expected exactly one site, got %#v", construction)
	}
	site0, _ := na.AsMap(sites[0])
	siteX, siteZ := int(na.AsNumber(site0["x"])), int(na.AsNumber(site0["z"]))

	// Free the sole GABP slot before the service starts its own bridge
	// session: this does NOT call games_stop, so the running game survives.
	if err := held.Release(); err != nil {
		return fmt.Errorf("close fixture-prep bridge session: %w", err)
	}

	// Launch the live Go player-control service, joined to the same GABS
	// configuration/game the harness above already started.
	profileDir := filepath.Join(output, "service-profile")
	if err := os.MkdirAll(profileDir, 0755); err != nil {
		return err
	}
	statePath := filepath.Join(output, "service.sqlite")
	serviceDir := filepath.Join(output, "service")
	if err := os.MkdirAll(serviceDir, 0755); err != nil {
		return err
	}
	argv := []string{
		"serve",
		"--profile", profileDir,
		"--gabs", gabsExecutable,
		"--config", cfg.Configuration,
		"--game", gameID,
		"--state", statePath,
		"--listen", "127.0.0.1:0",
		"--refresh", "1s",
		"--timeout", "15s",
	}
	argv = append(argv, na.FlightRecorderArgs(output, flightRecorder)...)
	report["service_argv"] = append([]string{rimgovernorBinary}, argv...)
	cmd := exec.CommandContext(ctx, rimgovernorBinary, argv...)
	// TEMPORARY: surface ClockScheduler.Step()'s branch tracing while
	// root-causing why the routine review never persists (G01.07b). Remove
	// this env injection once resolved.
	// Compose only the haul family so the receipt under test is unambiguous.
	cmd.Env = append(os.Environ(), "RIMGOVERNOR_CLOCK_DEBUG=1", "RIMGOVERNOR_ROUTINE_FAMILIES=haul")
	stdoutPipe, err := cmd.StdoutPipe()
	if err != nil {
		return err
	}
	stderrFile, err := os.Create(filepath.Join(serviceDir, "stderr.log"))
	if err != nil {
		return err
	}
	defer stderrFile.Close()
	cmd.Stderr = stderrFile
	if err := cmd.Start(); err != nil {
		return fmt.Errorf("start rimgovernor serve: %w", err)
	}
	report["service_pid"] = cmd.Process.Pid
	serviceDone := make(chan error, 1)
	go func() { serviceDone <- cmd.Wait() }()
	serviceStopped, serviceExited := false, false
	var serviceExit error
	// exited is the journal waits' na.Wait.Terminal: a service that died on
	// its own ends a wait at once instead of letting it stall out.
	exited := func() error {
		if !serviceExited {
			select {
			case serviceExit = <-serviceDone:
				serviceExited = true
			default:
				return nil
			}
		}
		if serviceExit == nil {
			return fmt.Errorf("service %d exited with status 0", cmd.Process.Pid)
		}
		return fmt.Errorf("service %d exited: %w", cmd.Process.Pid, serviceExit)
	}
	stopService := func() {
		if serviceStopped {
			return
		}
		serviceStopped = true
		if serviceExited {
			return
		}
		if cmd.ProcessState == nil {
			_ = cmd.Process.Kill()
		}
		<-serviceDone
	}
	defer stopService()
	w := na.Wait{Stall: na.StallBudget(), Terminal: exited}

	reader := bufio.NewReader(stdoutPipe)
	firstLine, err := reader.ReadString('\n')
	if err != nil {
		return fmt.Errorf("read service startup line: %w", err)
	}
	const prefix = "RimGovernor Go player service: "
	firstLine = strings.TrimSpace(firstLine)
	if !strings.HasPrefix(firstLine, prefix) {
		return fmt.Errorf("unexpected service startup line: %q", firstLine)
	}
	serviceURL := strings.TrimPrefix(firstLine, prefix)
	report["service_url"] = serviceURL
	stdoutLogFile, err := os.Create(filepath.Join(serviceDir, "stdout.log"))
	if err != nil {
		return err
	}
	defer stdoutLogFile.Close()
	go func() { _, _ = io.Copy(stdoutLogFile, reader) }()

	httpClient := &http.Client{Timeout: 20 * time.Second}
	requestCounter := 0
	apiCall := func(method, path string, body map[string]any, token string) (map[string]any, int, error) {
		var reqBody io.Reader
		if body != nil {
			data, err := json.Marshal(body)
			if err != nil {
				return nil, 0, err
			}
			reqBody = bytes.NewReader(data)
		}
		req, err := http.NewRequestWithContext(ctx, method, serviceURL+path, reqBody)
		if err != nil {
			return nil, 0, err
		}
		if body != nil {
			req.Header.Set("Content-Type", "application/json")
		}
		if token != "" {
			req.Header.Set("X-RimGovernor-Player", token)
		}
		req.Header.Set("Origin", serviceURL)
		resp, err := httpClient.Do(req)
		if err != nil {
			return nil, 0, err
		}
		defer resp.Body.Close()
		data, err := io.ReadAll(resp.Body)
		if err != nil {
			return nil, 0, err
		}
		requestCounter++
		_ = os.WriteFile(filepath.Join(serviceDir, fmt.Sprintf("http-%04d.json", requestCounter)), mustJSON(map[string]any{
			"method": method, "path": path, "request": body, "status": resp.StatusCode, "response": json.RawMessage(data),
		}), 0644)
		var out map[string]any
		if len(data) > 0 {
			if err := json.Unmarshal(data, &out); err != nil {
				return nil, resp.StatusCode, fmt.Errorf("decode %s %s: %w", method, path, err)
			}
		}
		return out, resp.StatusCode, nil
	}

	health, status, err := apiCall("GET", "/api/health", nil, "")
	if err != nil {
		return err
	}
	if status != 200 || na.AsString(health["service"]) != "rimgovernor" || na.AsString(health["backend"]) != "go" || int(na.AsNumber(health["pid"])) != cmd.Process.Pid {
		return fmt.Errorf("unexpected /api/health: status=%d body=%#v", status, health)
	}
	session, status, err := apiCall("GET", "/api/player/session", nil, "")
	if err != nil {
		return err
	}
	if status != 200 || na.AsString(session["mode"]) != "explicit-player" || na.AsString(session["token"]) == "" {
		return fmt.Errorf("unexpected /api/player/session: status=%d body=%#v", status, session)
	}
	token := na.AsString(session["token"])

	// Wait for the service's own separate GABS-attached bridge session to
	// discover the same running game and identity the harness already loaded.
	deadline := time.Now().Add(90 * time.Second)
	var state map[string]any
	for {
		state, status, err = apiCall("GET", "/api/state", nil, "")
		if err != nil {
			return err
		}
		if status != 200 {
			return fmt.Errorf("unexpected /api/state status=%d body=%#v", status, state)
		}
		if connected, _ := na.AsBool(state["connected"]); connected {
			if svcIdentity, ok := na.AsMap(state["identity"]); ok && matchesIdentity(svcIdentity) {
				break
			}
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("service did not attach to the fixture's identity in time: %#v", state)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(500 * time.Millisecond):
		}
	}
	report["service_state_attached"] = state

	// Submit the one guarded-construction building plan whose acceptance
	// occupies MaintainStorage's own arbitration capacity slot as "the
	// accepted player project" -- see policy.RankDevelopment.
	submission, status, err := apiCall("POST", "/api/buildings/plans", map[string]any{
		"requestId": "routine-haul-construction-1",
		"expected":  identity,
		"building":  map[string]any{"defName": "Wall", "x": siteX, "z": siteZ, "rotation": "north", "stuff": "WoodLog"},
	}, token)
	if err != nil {
		return err
	}
	if status != 200 && status != 201 {
		return fmt.Errorf("unexpected building submission status=%d body=%#v", status, submission)
	}
	if na.AsString(submission["planId"]) == "" || na.AsString(submission["revision"]) == "" {
		return fmt.Errorf("unexpected building submission: %#v", submission)
	}
	report["submission"] = submission

	acquireBody := map[string]any{
		"requestId": "routine-haul-resume-1", "expected": identity,
	}
	acquired, status, err := apiCall("POST", "/api/player/control/resume", acquireBody, token)
	if err != nil {
		return err
	}
	if status != 200 {
		return fmt.Errorf("unexpected acquire status=%d body=%#v", status, acquired)
	}
	acquiredRecord, _ := na.AsMap(acquired["record"])
	if na.AsString(acquiredRecord["phase"]) != "running" {
		return fmt.Errorf("resume was not running: %#v", acquired)
	}
	report["acquired"] = acquired
	// Work preferences hang off the world's root plan (the live authority),
	// not the submitted guidance plan.
	acquiredState, _ := na.AsMap(acquired["state"])
	acquiredGeneration, _ := na.AsMap(acquiredState["generation"])
	rootPlanID := na.AsString(acquiredGeneration["plan"])
	if rootPlanID == "" {
		return fmt.Errorf("resume reported no root plan: %#v", acquired)
	}

	verifyStore, err := openStoreWithRetry(ctx, statePath)
	if err != nil {
		return fmt.Errorf("open verification store: %w", err)
	}
	defer verifyStore.Close()

	// Native player authority is a bounded generation, not a standing grant:
	// it legitimately lapses -- on a bounded clock window running out
	// (NativeControlRevocationReason.GenerationExhausted; see
	// go/cmd/rimgovernor/serve_clock.go's MaxTicks/LeaseMS) or on ANY native
	// clock event PollEvents does not recognize as benign
	// (clockPollInterrupts in go/internal/buildingruntime/clock_poll.go
	// allowlists only Started/SpeedChanged/HostilesCleared/
	// ForcePauseCleared and a Stopped tagged TICK_BUDGET or REQUESTED_PAUSE;
	// everything else, e.g. a random world letter like a colonist pregnancy
	// notification, is conservatively treated as a genuine interruption) --
	// and nothing re-acquires it automatically: Control.Acquire's own comment
	// is explicit that this is "an authenticated explicit-player entrypoint"
	// action, never something ClockScheduler.Step() does on its own. Observed
	// live: a fresh disposable colony can take an unrelated interruption hold
	// (a "Kimmy pregnant" letter) within the first couple of review cycles,
	// well before either haul even begins -- so a one-shot acquire is not
	// enough to cover even the initial ramp-up, let alone the two full haul
	// dispatches this run waits through. Started immediately after the first
	// acquire so it can recover from an interruption at any point, including
	// during the startup diagnostic window below.
	keepAlive := &authorityKeepAlive{apiCall: apiCall, identity: identity, token: token}
	keepAliveCtx, stopKeepAlive := context.WithCancel(ctx)
	var keepAliveWG sync.WaitGroup
	keepAliveWG.Add(1)
	go func() { defer keepAliveWG.Done(); keepAlive.run(keepAliveCtx) }()
	defer func() {
		stopKeepAlive()
		keepAliveWG.Wait()
		report["authority_reacquisitions"] = keepAlive.snapshot()
	}()

	// Diagnostic: confirm the service's own clock scheduler actually starts
	// stepping (mode reaches "automate") and a routine review gets persisted
	// before waiting on the routine review journal directly -- if the
	// scheduler never reaches automate, no review will ever be written and
	// waitHaulMethod would otherwise just time out with no clue why. Runs
	// concurrently with keepAlive above, so an early benign interruption
	// (see its comment) gets a real chance to be recovered from within this
	// same window rather than failing the whole run outright.
	diagDeadline := time.Now().Add(60 * time.Second)
	var diagnostics []map[string]any
	sawAutomate := false
	var review store.RoutineReview
	for time.Now().Before(diagDeadline) {
		st, _, stErr := apiCall("GET", "/api/state", nil, "")
		clk, _, clkErr := apiCall("GET", "/api/player/clock", nil, "")
		entry := map[string]any{"state": st, "clock": clk}
		if stErr != nil {
			entry["state_error"] = stErr.Error()
		}
		if clkErr != nil {
			entry["clock_error"] = clkErr.Error()
		}
		diagnostics = append(diagnostics, entry)
		if na.AsString(st["mode"]) == "automate" {
			sawAutomate = true
		}
		if r, err := verifyStore.LoadRoutineReview(ctx); err == nil {
			review = r
			if review.Revision > 0 {
				break
			}
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(2 * time.Second):
		}
	}
	report["diagnostic_post_acquire"] = diagnostics
	reviewData, _ := json.Marshal(review)
	report["diagnostic_routine_review"] = json.RawMessage(reviewData)
	if !sawAutomate {
		return fmt.Errorf("service never reached automate mode after acquire; see diagnostic_post_acquire in the report")
	}
	if review.Revision == 0 {
		return fmt.Errorf("service reached automate mode but the routine review was never persisted (revision 0); see diagnostic_post_acquire/diagnostic_routine_review")
	}

	// Item 1: a worker is assigned and hauls it into shared storage; poll the
	// production journal for the goal binding, the committed method's plan,
	// and its Completed stage -- these transitions are set only by the
	// production executor's own native observation of completion, the same
	// mechanism the live game and the live service just exercised for real.
	goalID, method1, err := waitHaulMethod(ctx, verifyStore, w, "", nil)
	if err != nil {
		return fmt.Errorf("first haul method: %w", err)
	}
	report["goal_id"] = string(goalID)
	item1, method1, renewals1, err := waitHaulItem(ctx, verifyStore, w, goalID, method1)
	if err != nil {
		return fmt.Errorf("first haul completion: %w", err)
	}
	report["first_haul_incidental_renewals"] = renewals1
	if !na.Contains(itemIDs, item1) {
		return fmt.Errorf("first haul moved an unexpected item %q", item1)
	}
	item2Expected := itemIDs[0]
	if item1 == itemIDs[0] {
		item2Expected = itemIDs[1]
	}
	report["first_haul_item"] = item1

	// Baseline for the "no duplicate/conflicting order" checks below: the
	// committed method count once the first haul has genuinely completed,
	// including any incidental authority-discontinuity renewals absorbed
	// above -- not a hardcoded 1, since those renewals legitimately add
	// extra committed methods to the same still-live goal.
	baselineGoal, err := verifyStore.LoadGoal(ctx, goalID)
	if err != nil {
		return fmt.Errorf("load goal after first haul completion: %w", err)
	}
	baselineMethodCount := len(baselineGoal.Methods)

	// Interruption: the player revokes the single eligible hauler's Hauling
	// priority before the renewed deficit (the second item) is dispatched.
	// RoutineHaulPlanner must neither dispatch a new haul while overridden
	// nor double-issue once the override lifts.
	preferences, status, err := apiCall("GET", "/api/player/work-preferences?planId="+rootPlanID, nil, "")
	if err != nil {
		return err
	}
	if status != 200 {
		return fmt.Errorf("unexpected work-preferences read status=%d body=%#v", status, preferences)
	}
	baseRevision := na.AsString(preferences["revision"])
	revokeBody := map[string]any{
		"requestId": "routine-haul-revoke-hauling", "planId": rootPlanID, "expected": identity,
		"expectedRevision": baseRevision,
		"overrides":        []map[string]any{{"pawn": haulerID, "work": "Hauling", "priority": 0}},
	}
	revoked, status, err := apiCall("POST", "/api/player/work-preferences/replace", revokeBody, token)
	if err != nil {
		return err
	}
	if status != 200 {
		return fmt.Errorf("unexpected work-preferences revoke status=%d body=%#v", status, revoked)
	}
	report["revoked_hauling"] = revoked

	// Observe several review cycles: no new method should appear for the
	// renewed deficit while the only eligible hauler is overridden off.
	quietDeadline := time.Now().Add(20 * time.Second)
	for time.Now().Before(quietDeadline) {
		goal, err := verifyStore.LoadGoal(ctx, goalID)
		if err != nil {
			return fmt.Errorf("poll during interruption: %w", err)
		}
		if len(goal.Methods) > baselineMethodCount {
			return fmt.Errorf("RoutineHaulPlanner dispatched a new haul while the only eligible hauler's Hauling priority was revoked: %#v", goal.Methods)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(1 * time.Second):
		}
	}
	report["interruption_held"] = true

	// Clear the override: hauling for the second item must now proceed,
	// exactly once, with no duplicate/conflicting order.
	clearedRevision := na.AsString(revoked["revision"])
	restoreBody := map[string]any{
		"requestId": "routine-haul-restore-hauling", "planId": rootPlanID, "expected": identity,
		"expectedRevision": clearedRevision, "overrides": []map[string]any{},
	}
	restored, status, err := apiCall("POST", "/api/player/work-preferences/replace", restoreBody, token)
	if err != nil {
		return err
	}
	if status != 200 {
		return fmt.Errorf("unexpected work-preferences restore status=%d body=%#v", status, restored)
	}
	report["restored_hauling"] = restored

	_, method2, err := waitHaulMethod(ctx, verifyStore, w, goalID, &method1)
	if err != nil {
		return fmt.Errorf("second haul method: %w", err)
	}
	item2, _, renewals2, err := waitHaulItem(ctx, verifyStore, w, goalID, method2)
	if err != nil {
		return fmt.Errorf("second haul completion: %w", err)
	}
	report["second_haul_incidental_renewals"] = renewals2
	if item2 != item2Expected {
		return fmt.Errorf("second haul moved item %q, expected the renewed deficit %q", item2, item2Expected)
	}
	finalGoal, err := verifyStore.LoadGoal(ctx, goalID)
	if err != nil {
		return err
	}
	// One deliberate second dispatch on top of the baseline, plus whatever
	// incidental authority-discontinuity renewals waitHaulItem transparently
	// absorbed while waiting for it -- still no duplicate/conflicting order,
	// just accounting for legitimate renewals rather than a hardcoded 2.
	wantMethodCount := baselineMethodCount + 1 + renewals2
	if len(finalGoal.Methods) != wantMethodCount {
		return fmt.Errorf("expected exactly %d committed haul methods (no duplicates; baseline=%d incidental_renewals=%d+%d), got %d: %#v", wantMethodCount, baselineMethodCount, renewals1, renewals2, len(finalGoal.Methods), finalGoal.Methods)
	}
	report["second_haul_item"] = item2

	if err := na.AssertRoutineRunning(func(method, path string) (map[string]any, error) {
		v, status, err := apiCall(method, path, nil, "")
		if err != nil {
			return nil, err
		}
		if status != 200 {
			return nil, fmt.Errorf("%s %s: status %d", method, path, status)
		}
		return v, nil
	}); err != nil {
		return err
	}

	// Stop the service and its own bridge session to free the sole GABP slot
	// again, then reattach the harness session (the running game is
	// untouched) to take an independent native read of the stored outcome --
	// both hauled stacks merged into the one legal stockpile cell,
	// unforbidden. Reattach retries while the killed service's own GABS
	// subprocess frees the slot.
	verifyStore.Close()
	stopService()
	finalClient, err := held.Reattach(ctx)
	if err != nil {
		return fmt.Errorf("reopen bridge session for final native check: %w", err)
	}
	finalHarness := na.NewHarness(finalClient, output)

	afterBoth, err := finalHarness.Call(ctx, "after-both-hauls", "test/storage_haul_control", map[string]any{
		"colonyId": identity["colonyId"], "loadToken": identity["loadToken"], "mapId": identity["mapId"],
		"byCell": true, "x": storageX, "z": storageZ,
	})
	if err != nil {
		return err
	}
	if spawned, _ := na.AsBool(afterBoth["spawned"]); !spawned || int(na.AsNumber(afterBoth["count"])) < 50 {
		return fmt.Errorf("storage cell does not show both hauled items merged: %#v", afterBoth)
	}
	if inStockpile, _ := na.AsBool(afterBoth["inStockpile"]); !inStockpile {
		return fmt.Errorf("storage cell is not registered as the legal stockpile: %#v", afterBoth)
	}
	if forbidden, _ := na.AsBool(afterBoth["forbidden"]); forbidden {
		return fmt.Errorf("stored items unexpectedly forbidden: %#v", afterBoth)
	}
	report["after_both_hauls_storage"] = afterBoth

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}

// authorityKeepAlive keeps the harness's player authority granted for the
// full multi-minute duration of this acceptance run. Native player authority
// is a bounded generation (see the acquire comment in run()), so a run that
// waits through two full haul dispatches must behave like a real
// continuously-automating caller: notice whenever /api/state falls out of
// "automate" mode and re-acquire, rather than assuming a single acquire made
// at startup holds for the rest of the run.
type authorityKeepAlive struct {
	apiCall  func(method, path string, body map[string]any, token string) (map[string]any, int, error)
	identity map[string]any
	token    string

	mu                sync.Mutex
	attempts          int
	reacquired        int
	acknowledged      int
	acknowledgeFailed int
	lastError         string
}

func (k *authorityKeepAlive) snapshot() map[string]any {
	k.mu.Lock()
	defer k.mu.Unlock()
	out := map[string]any{
		"attempts": k.attempts, "reacquired": k.reacquired,
		"acknowledged": k.acknowledged, "acknowledge_failed": k.acknowledgeFailed,
	}
	if k.lastError != "" {
		out["last_error"] = k.lastError
	}
	return out
}

func (k *authorityKeepAlive) run(ctx context.Context) {
	for {
		select {
		case <-ctx.Done():
			return
		case <-time.After(2 * time.Second):
		}
		state, status, err := k.apiCall("GET", "/api/state", nil, "")
		if err != nil || status != 200 {
			continue
		}
		if na.AsString(state["mode"]) == "automate" {
			continue
		}
		// PollEvents (go/internal/buildingruntime/clock_poll.go) disables
		// authority the instant an unrecognized native clock event is seen
		// (e.g. a random world letter -- observed live: a colonist's own
		// pregnancy notification, nothing to do with this test), and it
		// keeps re-disabling on *every* subsequent poll cycle for as long as
		// that event's hold sits unacknowledged in the review journal --
		// independent of and unaffected by re-Acquiring. So a hold must be
		// acknowledged first (the same "player reviewed and dismissed the
		// interruption" action a real continuously-automating caller would
		// take), or every Acquire below would just be re-disabled within one
		// poll interval, never giving a review cycle time to persist.
		if clk, clkStatus, clkErr := k.apiCall("GET", "/api/player/clock", nil, ""); clkErr == nil && clkStatus == 200 {
			if holds := na.AsSlice(clk["holds"]); len(holds) > 0 {
				ackBody := map[string]any{
					"requestId":        fmt.Sprintf("routine-haul-ack-%d", time.Now().UnixNano()),
					"expectedRevision": na.AsString(clk["revision"]),
					"throughCursor":    na.AsString(clk["inboxCursor"]),
				}
				if _, ackStatus, ackErr := k.apiCall("POST", "/api/player/clock/acknowledge", ackBody, k.token); ackErr != nil || ackStatus != 200 {
					k.mu.Lock()
					if ackErr != nil {
						k.lastError = "acknowledge: " + ackErr.Error()
					} else {
						k.lastError = fmt.Sprintf("acknowledge status=%d", ackStatus)
					}
					k.acknowledgeFailed++
					k.mu.Unlock()
				} else {
					k.mu.Lock()
					k.acknowledged++
					k.mu.Unlock()
				}
			}
		}
		k.mu.Lock()
		k.attempts++
		k.mu.Unlock()
		body := map[string]any{
			"requestId": fmt.Sprintf("routine-haul-reacquire-%d", time.Now().UnixNano()),
			"expected":  k.identity,
		}
		acquired, status, err := k.apiCall("POST", "/api/player/control/resume", body, k.token)
		if err != nil {
			k.mu.Lock()
			k.lastError = err.Error()
			k.mu.Unlock()
			continue
		}
		record, _ := na.AsMap(acquired["record"])
		if status != 200 {
			k.mu.Lock()
			k.lastError = fmt.Sprintf("reacquire status=%d body=%#v", status, acquired)
			k.mu.Unlock()
			continue
		}
		if na.AsString(record["phase"]) != "running" {
			k.mu.Lock()
			k.lastError = fmt.Sprintf("reacquire not running: %#v", acquired)
			k.mu.Unlock()
			continue
		}
		k.mu.Lock()
		k.reacquired++
		k.lastError = ""
		k.mu.Unlock()
	}
}

// waitHaulMethod polls the durable routine review for a MaintainStorage goal
// binding and its (newly appearing) committed haul method, mirroring exactly
// what buildingruntime.RoutineHaulPlanner.step itself reads: review.Goals for
// the MaintainStorage Need, then that goal's Methods. previousMethod, when
// non-nil, is excluded so this waits specifically for a fresh renewal rather
// than re-observing the same method.
func waitHaulMethod(ctx context.Context, s *store.Store, w na.Wait, knownGoal domain.GoalID, previousMethod *domain.GoalMethod) (domain.GoalID, domain.GoalMethod, error) {
	var foundGoal domain.GoalID
	var found domain.GoalMethod
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		var goalID domain.GoalID
		for _, binding := range review.Goals {
			if binding.Need == policy.MaintainStorage {
				goalID = binding.Goal
				break
			}
		}
		if goalID == "" || knownGoal != "" && goalID != knownGoal {
			return na.Signature("goal", goalID), false, nil
		}
		goal, err := s.LoadGoal(ctx, goalID)
		if err != nil && !errors.Is(err, store.ErrNotFound) {
			return "", false, err
		}
		for _, method := range goal.Methods {
			if previousMethod == nil || method.Method != previousMethod.Method {
				foundGoal, found = goalID, method
				return "", true, nil
			}
		}
		return na.Signature("goal", goalID, goal.Goal.Epoch, len(goal.Methods)), false, nil
	})
	if err != nil {
		return "", domain.GoalMethod{}, err
	}
	return foundGoal, found, nil
}

// waitHaulCompleted polls one haul plan until its single action reaches a
// terminal stage. Completed returns the hauled item's thing id. Unsuccessful
// is always a genuine failure.
//
// Cancelled needs a closer look: domain.Progress.observe (go/internal/domain/progress.go)
// sets Stage=Cancelled from its EffectAbsent branch specifically when the
// action's *dispatch-time* GenerationSnapshot no longer matches the current
// one -- e.g. because the load token or native generation advanced. That happens whenever this
// harness's own authorityKeepAlive reacquires player authority mid-dispatch,
// which live observation confirms a disposable headless colony can trigger
// well before either haul even begins (an incidental native interruption --
// a random letter, not anything this test scripted -- see authorityKeepAlive's
// doc comment above). domain.Progress.Observe's own doc comment explains why
// this is correct, not a bug: it deliberately refuses to resolve a dispatch
// across an authority discontinuity, rather than risk misattributing its
// effect. So this Cancelled shape is not a test failure -- it is the executor
// safely abandoning an in-flight attempt, and the still-live MaintainStorage
// deficit is expected to get a fresh method on the next routine review. That
// signal is returned as incidentalCancel=true so the caller can wait for the
// renewal instead of failing outright -- see waitHaulItem.
//
// Any other Cancelled shape (Effect not observed as Absent) is treated as a
// genuine failure, same as Unsuccessful.
func waitHaulCompleted(ctx context.Context, s *store.Store, w na.Wait, planID domain.PlanID) (item string, incidentalCancel bool, err error) {
	_, err = na.WaitPlan(ctx, s, w, planID, func(state store.PlanState) (string, bool, error) {
		actions := state.Spec.Actions()
		if len(actions) != 1 || len(state.Progress) != 1 {
			return "", false, fmt.Errorf("unexpected haul plan shape: %d actions, %d progress", len(actions), len(state.Progress))
		}
		haul, ok := actions[0].Haul()
		if !ok {
			return "", false, fmt.Errorf("haul plan action is not a haul action")
		}
		view := state.Progress[0].View()
		switch view.Stage {
		case domain.Completed:
			item = haul.Thing()
			return "", true, nil
		case domain.Unsuccessful:
			return "", false, fmt.Errorf("haul plan %s reached unsuccessful instead of completed", planID)
		case domain.Cancelled:
			if effect, known := view.Effect.Value(); known && effect == domain.EffectAbsent {
				incidentalCancel = true
				return "", true, nil
			}
			return "", false, fmt.Errorf("haul plan %s reached cancelled instead of completed", planID)
		}
		return na.PlanSignature(state), false, nil
	})
	if err != nil {
		return "", false, err
	}
	return item, incidentalCancel, nil
}

// waitHaulItem waits for method's plan to complete, transparently following
// any renewal caused by an incidental authority-discontinuity cancellation
// (see waitHaulCompleted) by waiting for the routine reviewer to naturally
// bind a fresh method to the same still-live goal -- exactly the production
// recovery behavior a real player would see -- and retrying against that
// plan instead of failing. Returns the hauled item id, the method whose plan
// actually completed (which differs from the one passed in whenever a
// renewal occurred), and how many incidental renewals were absorbed so
// callers can adjust their own method-count bookkeeping.
func waitHaulItem(ctx context.Context, s *store.Store, w na.Wait, goalID domain.GoalID, method domain.GoalMethod) (item string, final domain.GoalMethod, renewals int, err error) {
	for {
		item, incidental, err := waitHaulCompleted(ctx, s, w, method.Plan)
		if err != nil {
			return "", method, renewals, err
		}
		if !incidental {
			return item, method, renewals, nil
		}
		renewals++
		_, next, err := waitHaulMethod(ctx, s, w, goalID, &method)
		if err != nil {
			return "", method, renewals, fmt.Errorf("waiting for renewed haul method after incidental interruption #%d: %w", renewals, err)
		}
		method = next
	}
}

func openStoreWithRetry(ctx context.Context, path string) (*store.Store, error) {
	deadline := time.Now().Add(60 * time.Second)
	for {
		s, err := store.Open(ctx, path)
		if err == nil {
			return s, nil
		}
		if time.Now().After(deadline) {
			return nil, err
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(500 * time.Millisecond):
		}
	}
}

func sha256File(path string) (string, error) {
	f, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer f.Close()
	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

func mustJSON(v any) []byte {
	data, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return []byte("{}")
	}
	return data
}
