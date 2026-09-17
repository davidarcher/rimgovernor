// Command moodreliefaccept exercises the EnsureMood-* native need-relief
// dispatch vertical (G01.07e) end to end against a live game: a disposable
// fixture pawn with a genuinely deficient need, a matching current job and
// timetable assignment, native dispatch (an actual JobGiver_GetJoy/GetFood/
// GetRest job issued through the same rimgovernor/operations_execute wire
// contract Go's buildingruntime.MoodReliefBoundary drives via
// bridge.MoodReliefWriter), real game ticks carrying the need back above the
// native recovery threshold, and its observation/replay semantics. Uses a
// private disposable fixture (test/mood_setup) since a deterministic
// deficient-need pawn with a known current job and timetable slot cannot be
// relied on from native random pawn generation and colony state, mirroring
// surgeryaccept's and questfulfillaccept's own fixture-first pattern.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"strconv"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const sessionOwner = "native-mood-relief-acceptance"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-mood-relief-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 20*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-mood-relief-acceptance"
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
	report := na.NewReport("Native EnsureMood-* relief dispatch vertical: an actual JobGiver_GetJoy/GetFood/GetRest "+
		"job issued through the typed operations contract, exact CAS/stale-identity and stale-fencing refusal, "+
		"real need recovery observed via native ticks, replay idempotency and durable lookup.", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, !*rendered, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID string, headless bool, report na.Report) error {
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
	held, err := na.OpenGame(ctx, cfg)
	if err != nil {
		return err
	}
	defer held.Close(report)
	client := held.Client
	h := na.NewHarness(client, output)

	if _, err := na.StartDebugGame(ctx, h, nil, na.QuietRequired); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
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

	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	report["discovery"] = names
	if !na.Contains(names, "rimgovernor/operations_execute") {
		return fmt.Errorf("missing rimgovernor/operations_execute in discovery")
	}

	// SetMode(Auto) at the current generation (#52): no lease, the granted
	// body's context.nativeGeneration is what preconditions carry.
	grant, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity)
	if err != nil {
		return err
	}
	grantContext, _ := na.AsMap(grant["context"])

	// pawnRow reads the exact fixture pawn through the same
	// rimgovernor/observations_list_pawns call bridge.ReadPawns issues, so
	// the snapshot token, current job load ID and timetable assignment used
	// below are exactly what buildingruntime.moodReliefDispatchFacts itself
	// would decode from this call.
	pawnRow := func(label, pawnID string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{
			"scope":   map[string]any{"expectedIdentity": identity},
			"filter":  map[string]any{"ids": []string{pawnID}, "includeDead": true},
			"details": map[string]any{"needs": true, "settings": true},
			"page":    map[string]any{"limit": 1},
		})
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, err
		}
		rows := na.AsSlice(observed["pawns"])
		if len(rows) != 1 {
			return nil, fmt.Errorf("%s: expected exactly one observed pawn, got %#v", label, observed)
		}
		row, _ := na.AsMap(rows[0])
		pawn, _ := na.AsMap(row["pawn"])
		if na.AsString(pawn["id"]) != pawnID {
			return nil, fmt.Errorf("%s: unexpected pawn row: %#v", label, row)
		}
		return row, nil
	}

	// fencing extracts the pawn snapshot token, current job load ID (as the
	// non-idle ExpectedJob form the fixture always leaves the pawn in, since
	// test/mood_setup always starts an ordinary Wait job) and the uniform
	// timetable assignment def name mirroring
	// buildingruntime.moodReliefDispatchFacts's decode of the same wire
	// fields (boundary.JobEvidenceExpectedJob, boundary.ExpectedScheduleDef).
	fencing := func(row map[string]any) (token string, jobID int64, scheduleDef string, joyLevel float64, err error) {
		pawn, _ := na.AsMap(row["pawn"])
		snapshot, _ := na.AsMap(pawn["snapshot"])
		token = na.AsString(snapshot["token"])
		job, _ := na.AsMap(row["job"])
		loadID := na.AsString(job["loadId"])
		jobID, convErr := strconv.ParseInt(loadID, 10, 32)
		if convErr != nil {
			return "", 0, "", 0, fmt.Errorf("fencing: unparsable job load id %q: %w", loadID, convErr)
		}
		settings, _ := na.AsMap(row["settings"])
		slots := na.AsSlice(settings["schedule"])
		if len(slots) == 0 {
			return "", 0, "", 0, fmt.Errorf("fencing: missing timetable schedule: %#v", row)
		}
		slot, _ := na.AsMap(slots[0])
		scheduleDef = na.AsString(slot["assignmentDefName"])
		needs, _ := na.AsMap(row["needs"])
		joyLevel = na.AsNumber(needs["joy"])
		if token == "" || scheduleDef == "" {
			return "", 0, "", 0, fmt.Errorf("fencing: missing token or schedule def: %#v", row)
		}
		return token, jobID, scheduleDef, joyLevel, nil
	}

	setup := func(label, scenario string) (pawnID string, err error) {
		prepared, err := h.Call(ctx, label, "test/mood_setup", map[string]any{"scenario": scenario})
		if err != nil {
			return "", err
		}
		if success, _ := na.AsBool(prepared["success"]); !success {
			return "", fmt.Errorf("%s: mood_setup refused: %#v", label, prepared)
		}
		pawnID = na.AsString(prepared["pawn"])
		if pawnID == "" {
			return "", fmt.Errorf("%s: mood_setup: missing fixture pawn id: %#v", label, prepared)
		}
		return pawnID, nil
	}

	buildOperation := func(pawnID, pToken string, jobID int64, scheduleDef string) map[string]any {
		return map[string]any{"relieveNeed": map[string]any{
			"pawn":                map[string]any{"entityId": pawnID, "expectedSnapshotToken": pToken},
			"need":                "NEED_JOY",
			"expectedJob":         map[string]any{"jobId": jobID},
			"expectedScheduleDef": scheduleDef,
		}}
	}
	buildRequest := func(actionID, attemptID string, operation map[string]any) map[string]any {
		return map[string]any{
			"precondition": map[string]any{
				"identity": identity, "expectedGeneration": grantContext["nativeGeneration"],
				"attempt": map[string]any{"controllerSessionId": sessionOwner, "actionId": actionID, "attemptId": attemptID},
			},
			"operation": operation,
		}
	}

	// --- Positive path: joy relief ---
	pawnID, err := setup("setup-joy", "joy")
	if err != nil {
		return err
	}
	report["fixture_pawn"] = pawnID

	rowBefore, err := pawnRow("target-before", pawnID)
	if err != nil {
		return err
	}
	pawnToken, jobID, scheduleDef, joyBefore, err := fencing(rowBefore)
	if err != nil {
		return err
	}
	report["needs_joy_before"] = joyBefore
	if joyBefore >= 0.5 {
		return fmt.Errorf("target-before: expected a deficient joy need, got %v", joyBefore)
	}

	// Refusal 1: a stale current-job fencing value (as if the pawn's job
	// changed since inspection) must be refused rather than silently
	// dispatched against whatever job is actually current.
	staleJobRequest := buildRequest("mood-relief-stale-job", "1", buildOperation(pawnID, pawnToken, jobID+987654, scheduleDef))
	if code, err := failureCode(ctx, h, "stale-job", staleJobRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("stale-job: expected FAILURE_CODE_INVALID_REQUEST, got %q", code)
	}

	// Refusal 2: a stale timetable fencing value must be refused, proving
	// ExpectedScheduleDef (boundary.ExpectedScheduleDef) is load-bearing.
	staleScheduleRequest := buildRequest("mood-relief-stale-schedule", "1", buildOperation(pawnID, pawnToken, jobID, "Work"))
	if code, err := failureCode(ctx, h, "stale-schedule", staleScheduleRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("stale-schedule: expected FAILURE_CODE_INVALID_REQUEST, got %q", code)
	}

	// Refusal 3: a stale per-pawn CAS snapshot token must be refused, proving
	// the generic NativePawnControlState token (shared with every other
	// pawn-order family) is load-bearing here too, mirroring
	// surgeryaccept's/questfulfillaccept's own stale-identity case.
	staleTokenRequest := buildRequest("mood-relief-stale-token", "1", buildOperation(pawnID, "stale-pawn-token-00000000000000000000000000000000", jobID, scheduleDef))
	if code, err := failureCode(ctx, h, "stale-token", staleTokenRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_OWNER_CONFLICT" && code != "FAILURE_CODE_NOT_FOUND" {
		return fmt.Errorf("stale-token: expected an owner-conflict or not-found refusal, got %q", code)
	}

	// Preview: accepted, but never mutates the live job.
	previewReply, err := h.Wire(ctx, "preview", "operations_preview", map[string]any{
		"identity": identity, "operation": buildOperation(pawnID, pawnToken, jobID, scheduleDef),
	})
	if err != nil {
		return err
	}
	evaluated, ok := na.AsMap(previewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview: expected an evaluated reply, got %#v", previewReply)
	}
	if previewAccepted, _ := na.AsBool(evaluated["accepted"]); !previewAccepted {
		return fmt.Errorf("preview: expected the relief to be accepted, got %#v", evaluated)
	}
	afterPreviewRow, err := pawnRow("target-after-preview", pawnID)
	if err != nil {
		return err
	}
	afterPreviewJob, _ := na.AsMap(afterPreviewRow["job"])
	if na.AsString(afterPreviewJob["loadId"]) != strconv.FormatInt(jobID, 10) {
		return fmt.Errorf("target-after-preview: expected an unchanged current job from a dry-run preview: %#v", afterPreviewRow)
	}

	// Execute: the real native JobGiver_GetJoy dispatch.
	reliefRequest := buildRequest("mood-relief-execute", "1", buildOperation(pawnID, pawnToken, jobID, scheduleDef))
	receiptReply, err := h.Wire(ctx, "execute", "operations_execute", reliefRequest)
	if err != nil {
		return err
	}
	_, receipt, err := na.Outcome(receiptReply, "receipt")
	if err != nil {
		return err
	}
	applied, ok := na.AsMap(receipt["applied"])
	if !ok {
		return fmt.Errorf("execute: expected an applied outcome, got %#v", receipt)
	}
	appliedObserved, _ := na.AsMap(applied["observed"])
	appliedJob, _ := na.AsMap(appliedObserved["job"])
	if na.AsString(appliedJob["pawnId"]) != pawnID {
		return fmt.Errorf("execute: unexpected applied relief evidence: %#v", appliedJob)
	}
	if issued, _ := na.AsBool(appliedJob["issued"]); !issued {
		return fmt.Errorf("execute: expected the native relief job to be issued, got %#v", appliedJob)
	}

	afterExecuteRow, err := pawnRow("target-after-execute", pawnID)
	if err != nil {
		return err
	}
	afterExecuteJob, _ := na.AsMap(afterExecuteRow["job"])
	if na.AsString(afterExecuteJob["loadId"]) == strconv.FormatInt(jobID, 10) {
		return fmt.Errorf("target-after-execute: expected the native current job to change from the fixture's Wait job: %#v", afterExecuteRow)
	}

	precondition, _ := na.AsMap(reliefRequest["precondition"])
	attempt := map[string]any{"identity": identity, "attempt": precondition["attempt"]}

	// Observe: run real game time forward at Fast until the joy need is
	// actually observed to recover to native's own 0.5 completion
	// threshold, matching the doc contract ("Positive cases require actual
	// rest, food and recreation recovery to at least 0.5"); absence of the
	// original job never proves recovery by itself.
	completedAll, err := na.ObserveCompleted(ctx, h, "observe", 2*na.TicksPerDay, attempt)
	if err != nil {
		return err
	}
	completedProgress := completedAll[0]
	completedEvidence, _ := na.AsMap(completedProgress["evidence"])
	completedJob, _ := na.AsMap(completedEvidence["job"])
	if na.AsString(completedJob["pawnId"]) != pawnID {
		return fmt.Errorf("observe: unexpected completed relief evidence: %#v", completedJob)
	}

	afterCompleteRow, err := pawnRow("target-after-complete", pawnID)
	if err != nil {
		return err
	}
	afterCompleteNeeds, _ := na.AsMap(afterCompleteRow["needs"])
	joyAfter := na.AsNumber(afterCompleteNeeds["joy"])
	report["needs_joy_after"] = joyAfter
	if joyAfter < 0.5 {
		return fmt.Errorf("target-after-complete: expected native joy need recovered to at least 0.5, got %v", joyAfter)
	}

	// Replay: the exact same attempt returns an identical receipt, and does
	// not attempt to issue a second native job.
	replayReply, err := h.Wire(ctx, "replay", "operations_execute", reliefRequest)
	if err != nil {
		return err
	}
	_, replay, err := na.Outcome(replayReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, receipt) {
		return fmt.Errorf("replay: replay of the same attempt returned a different receipt")
	}

	// Durable lookup: receipts_lookup independently returns the same receipt.
	lookupReply, err := h.Wire(ctx, "lookup", "receipts_lookup", attempt)
	if err != nil {
		return err
	}
	_, lookup, err := na.Outcome(lookupReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(lookup, receipt) {
		return fmt.Errorf("lookup: expected the same receipt as execute, got %#v", lookup)
	}

	// --- Negative fixtures: ineligible pawns are correctly refused, using
	// fresh, fast (no game-tick) fixture scenarios. ---

	forcedPawnID, err := setup("setup-forced", "forced")
	if err != nil {
		return err
	}
	forcedRow, err := pawnRow("target-forced", forcedPawnID)
	if err != nil {
		return err
	}
	forcedToken, forcedJobID, forcedScheduleDef, _, err := fencing(forcedRow)
	if err != nil {
		return err
	}
	forcedRequest := buildRequest("mood-relief-forced", "1", buildOperation(forcedPawnID, forcedToken, forcedJobID, forcedScheduleDef))
	if code, err := failureCode(ctx, h, "player-forced-job", forcedRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("player-forced-job: expected FAILURE_CODE_INVALID_REQUEST, got %q", code)
	}
	report["negative_forced_pawn"] = forcedPawnID

	mentalPawnID, err := setup("setup-mental", "mental")
	if err != nil {
		return err
	}
	mentalRow, err := pawnRow("target-mental", mentalPawnID)
	if err != nil {
		return err
	}
	// NativeMoodReliefOperations.Prepare checks pawn.InMentalState before it
	// ever compares the job/schedule fencing values, and the native reader
	// itself marks the current-job field unavailable while a mental state is
	// active (its own "current_job" issue), so this case only needs a valid
	// per-pawn CAS token -- any job/schedule fencing values are fine, since
	// the mental-break refusal must fire first regardless of their content.
	mentalPawn, _ := na.AsMap(mentalRow["pawn"])
	mentalSnapshot, _ := na.AsMap(mentalPawn["snapshot"])
	mentalToken := na.AsString(mentalSnapshot["token"])
	if mentalToken == "" {
		return fmt.Errorf("target-mental: missing pawn snapshot token: %#v", mentalRow)
	}
	mentalRequest := buildRequest("mood-relief-mental", "1", buildOperation(mentalPawnID, mentalToken, 0, "Anything"))
	if code, err := failureCode(ctx, h, "active-mental-break", mentalRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("active-mental-break: expected FAILURE_CODE_INVALID_REQUEST, got %q", code)
	}
	report["negative_mental_pawn"] = mentalPawnID

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}

// failureCode wires request through operations_execute and returns the
// failure code, mirroring surgeryaccept's/questfulfillaccept's helper of the
// same name.
func failureCode(ctx context.Context, h *na.Harness, label string, request map[string]any) (string, error) {
	reply, err := h.Wire(ctx, label, "operations_execute", request)
	if err != nil {
		return "", err
	}
	_, failure, err := na.Outcome(reply, "failure")
	if err != nil {
		return "", err
	}
	return na.AsString(failure["code"]), nil
}
