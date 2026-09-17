// Command surgeryaccept exercises the QueueSurgery vertical (G01.08) end to
// end against a live game: an exact patient missing a leg, a real practitioner
// and Wood stock, native queueing (an actual HealthCardUtility.CreateSurgeryBill
// call invoked through the same rimgovernor/operations_execute wire contract
// Go's buildingruntime.SurgeryBoundary drives) and its observation/replay
// semantics through to a real completed operation. Uses a private disposable
// fixture (test/surgery_prepare) since a deterministic missing-leg patient
// eligible for InstallPegLeg cannot be relied on from native random pawn
// generation, mirroring questfulfillaccept's own fixture-first pattern.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const sessionOwner = "native-surgery-acceptance"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-surgery-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 20*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-surgery-acceptance"
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
	report := na.NewReport("Native QueueSurgery vertical: an actual HealthCardUtility.CreateSurgeryBill queued "+
		"through the typed operations contract, exact CAS/stale-identity refusal, real completion via native work "+
		"selection, replay idempotency and durable lookup.", !*rendered)
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

	prepared, err := h.Call(ctx, "prepare", "test/surgery_prepare", map[string]any{})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(prepared["success"]); !success {
		return fmt.Errorf("surgery_prepare refused: %#v", prepared)
	}
	if na.AsString(prepared["colonyId"]) != na.AsString(identity["colonyId"]) ||
		na.AsString(prepared["loadToken"]) != na.AsString(identity["loadToken"]) ||
		na.AsNumber(prepared["mapId"]) != na.AsNumber(identity["mapId"]) {
		return fmt.Errorf("surgery_prepare identity does not match the fresh debug game")
	}
	report["prepared"] = prepared
	patientID := na.AsString(prepared["patientId"])
	recipe := na.AsString(prepared["recipe"])
	part := int32(na.AsNumber(prepared["part"]))
	if patientID == "" || recipe == "" {
		return fmt.Errorf("surgery_prepare: unexpected fixture identifiers: %#v", prepared)
	}

	statusReply, err := h.Wire(ctx, "authority-status", "authority_read_status", map[string]any{"identity": identity})
	if err != nil {
		return err
	}
	_, status, err := na.Outcome(statusReply, "status")
	if err != nil {
		return err
	}
	statusContext, _ := na.AsMap(status["context"])
	grantReply, err := h.Wire(ctx, "acquire", "authority_control", map[string]any{"acquire": map[string]any{
		"identity": identity, "expectedGeneration": statusContext["nativeGeneration"],
		"owner": map[string]any{"controllerSessionId": sessionOwner, "playerDirection": "1"}, "leaseMs": 30000,
	}})
	if err != nil {
		return err
	}
	_, grant, err := na.Outcome(grantReply, "granted")
	if err != nil {
		return err
	}
	grantContext, _ := na.AsMap(grant["context"])

	// patientRow reads the fixture's exact patient through the same
	// rimgovernor/observations_list_pawns call bridge.ReadTendPawns issues,
	// so the CAS token and care policy used below are exactly what the Go
	// boundary itself would compute from this call.
	patientRow := func(label string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{
			"scope":   map[string]any{"expectedIdentity": identity},
			"filter":  map[string]any{"ids": []string{patientID}, "includeDead": true},
			"details": map[string]any{"health": true, "equipment": true, "biography": true, "settings": true},
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
			return nil, fmt.Errorf("%s: expected exactly one observed patient, got %#v", label, observed)
		}
		row, _ := na.AsMap(rows[0])
		pawn, _ := na.AsMap(row["pawn"])
		if na.AsString(pawn["id"]) != patientID {
			return nil, fmt.Errorf("%s: unexpected patient row: %#v", label, row)
		}
		return row, nil
	}

	rowBefore, err := patientRow("target-before")
	if err != nil {
		return err
	}
	pawnBefore, _ := na.AsMap(rowBefore["pawn"])
	snapshotBefore, _ := na.AsMap(pawnBefore["snapshot"])
	patientToken := na.AsString(snapshotBefore["token"])
	settingsBefore, _ := na.AsMap(rowBefore["settings"])
	careWire, err := careToWire(na.AsString(settingsBefore["medicalCare"]))
	if err != nil {
		return err
	}
	if patientToken == "" {
		return fmt.Errorf("target-before: missing patient snapshot token: %#v", rowBefore)
	}

	// baseline previews the exact patient/recipe/part triple with no expected
	// health token or care supplied, mirroring bridge.ReadSurgeryTarget's own
	// unconstrained dry-run read, to establish the current native health
	// signature before any CAS-constrained call.
	baseline := func(label string) (string, bool, error) {
		reply, err := h.Wire(ctx, label, "operations_preview", map[string]any{
			"identity": identity,
			"operation": map[string]any{"queueSurgery": map[string]any{
				"patient":   map[string]any{"entityId": patientID, "expectedSnapshotToken": patientToken},
				"recipeDef": recipe, "partIndex": part,
			}},
		})
		if err != nil {
			return "", false, err
		}
		evaluated, ok := na.AsMap(reply["evaluated"])
		if !ok {
			return "", false, fmt.Errorf("%s: expected an evaluated reply, got %#v", label, reply)
		}
		projected, _ := na.AsMap(evaluated["projected"])
		surgery, _ := na.AsMap(projected["surgery"])
		health := na.AsString(surgery["healthToken"])
		if health == "" {
			return "", false, fmt.Errorf("%s: missing projected health token: %#v", label, evaluated)
		}
		accepted, _ := na.AsBool(evaluated["accepted"])
		return health, accepted, nil
	}
	healthToken, accepted, err := baseline("baseline")
	if err != nil {
		return err
	}
	if !accepted {
		return fmt.Errorf("baseline: expected the fixture recipe/part to be currently queueable")
	}

	buildOperation := func(pToken, hToken, care string) map[string]any {
		return map[string]any{"queueSurgery": map[string]any{
			"patient":   map[string]any{"entityId": patientID, "expectedSnapshotToken": pToken},
			"recipeDef": recipe, "partIndex": part,
			"expectedHealthToken": hToken, "expectedCare": care,
		}}
	}
	buildRequest := func(actionID, attemptID string, operation map[string]any) map[string]any {
		return map[string]any{
			"precondition": map[string]any{
				"identity": identity, "expectedGeneration": grantContext["nativeGeneration"], "leaseId": grant["leaseId"],
				"attempt": map[string]any{"controllerSessionId": sessionOwner, "actionId": actionID, "attemptId": attemptID},
			},
			"operation": operation,
		}
	}

	// Refusal 1: a stale health-signature token (as if inspected before some
	// other hediff change) must be refused rather than silently ignored.
	staleHealthRequest := buildRequest("surgery-stale-health", "1", buildOperation(patientToken, "stale-health-token-000000000000000000000000000000", careWire))
	if code, err := failureCode(ctx, h, "stale-health", staleHealthRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("stale-health: expected FAILURE_CODE_INVALID_REQUEST, got %q", code)
	}

	// Refusal 2: a stale patient snapshot token must be refused, proving the
	// generic per-pawn CAS token (NativePawnControlState, shared with every
	// other pawn-order family) is load-bearing here. NativeDraftProtocol.
	// Failure maps a StaleSnapshot guard result to FAILURE_CODE_OWNER_CONFLICT
	// (not stale-identity, which is reserved for colony/load/map mismatch).
	staleIdentityRequest := buildRequest("surgery-stale-identity", "1", buildOperation("stale-patient-token-00000000000000000000000000000", healthToken, careWire))
	if code, err := failureCode(ctx, h, "stale-identity", staleIdentityRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_OWNER_CONFLICT" && code != "FAILURE_CODE_NOT_FOUND" {
		return fmt.Errorf("stale-identity: expected an owner-conflict or not-found refusal, got %q", code)
	}

	// Preview: accepted, but never mutates the live bill stack.
	previewReply, err := h.Wire(ctx, "preview", "operations_preview", map[string]any{
		"identity": identity, "operation": buildOperation(patientToken, healthToken, careWire),
	})
	if err != nil {
		return err
	}
	evaluated, ok := na.AsMap(previewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview: expected an evaluated reply, got %#v", previewReply)
	}
	if previewAccepted, _ := na.AsBool(evaluated["accepted"]); !previewAccepted {
		return fmt.Errorf("preview: expected the surgery to be accepted, got %#v", evaluated)
	}
	afterPreviewRow, err := patientRow("target-after-preview")
	if err != nil {
		return err
	}
	afterPreviewHealth, _ := na.AsMap(afterPreviewRow["health"])
	if bills := na.AsSlice(afterPreviewHealth["surgeryBills"]); len(bills) != 0 {
		return fmt.Errorf("target-after-preview: expected no bill queued by a dry-run preview: %#v", afterPreviewRow)
	}

	// Execute: the real native HealthCardUtility.CreateSurgeryBill call.
	queueRequest := buildRequest("surgery-queue", "1", buildOperation(patientToken, healthToken, careWire))
	receiptReply, err := h.Wire(ctx, "execute", "operations_execute", queueRequest)
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
	appliedSurgery, _ := na.AsMap(appliedObserved["surgery"])
	if na.AsString(appliedSurgery["patientId"]) != patientID || na.AsString(appliedSurgery["recipeDef"]) != recipe {
		return fmt.Errorf("execute: unexpected applied surgery evidence: %#v", appliedSurgery)
	}
	if queued, _ := na.AsBool(appliedSurgery["queued"]); !queued {
		return fmt.Errorf("execute: expected the bill to be queued, got %#v", appliedSurgery)
	}

	afterExecuteRow, err := patientRow("target-after-execute")
	if err != nil {
		return err
	}
	afterExecuteHealth, _ := na.AsMap(afterExecuteRow["health"])
	if bills := na.AsSlice(afterExecuteHealth["surgeryBills"]); len(bills) != 1 {
		return fmt.Errorf("target-after-execute: expected exactly one native bill queued: %#v", afterExecuteRow)
	}

	precondition, _ := na.AsMap(queueRequest["precondition"])
	attempt := map[string]any{"identity": identity, "attempt": precondition["attempt"]}

	// Observe: run real game time forward at Fast until native work selection
	// carries the practitioner through the queued bill and the hediff change
	// is observed: the outcome must be observed separately; absence of a bill
	// never proves completion by itself.
	if _, err := h.Call(ctx, "resume", "rimworld/set_time_speed", map[string]any{"speed": "Fast", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	var completedProgress map[string]any
	deadline := time.Now().Add(10 * time.Minute)
	for completedProgress == nil {
		if time.Now().After(deadline) {
			return fmt.Errorf("observe: surgery did not complete within the polling deadline")
		}
		progressReply, err := h.Wire(ctx, "observe-poll", "receipts_observe_progress", attempt)
		if err != nil {
			return err
		}
		_, progress, err := na.Outcome(progressReply, "progress")
		if err != nil {
			return err
		}
		if unsuccessful, ok := na.AsMap(progress["unsuccessful"]); ok {
			return fmt.Errorf("observe: surgery became unsuccessful before completion: %#v", unsuccessful)
		}
		if completed, ok := na.AsMap(progress["completed"]); ok {
			completedProgress = completed
			break
		}
		time.Sleep(2 * time.Second)
	}
	if _, err := h.Call(ctx, "pause-after-complete", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	completedEvidence, _ := na.AsMap(completedProgress["evidence"])
	completedSurgery, _ := na.AsMap(completedEvidence["surgery"])
	if na.AsString(completedSurgery["patientId"]) != patientID || na.AsString(completedSurgery["recipeDef"]) != recipe {
		return fmt.Errorf("observe: unexpected completed surgery evidence: %#v", completedSurgery)
	}

	afterCompleteRow, err := patientRow("target-after-complete")
	if err != nil {
		return err
	}
	afterCompleteHealth, _ := na.AsMap(afterCompleteRow["health"])
	installed := false
	for _, raw := range na.AsSlice(afterCompleteHealth["hediffs"]) {
		hediff, _ := na.AsMap(raw)
		definition, _ := na.AsMap(hediff["definition"])
		if na.AsString(definition["defName"]) == "PegLeg" && int32(na.AsNumber(hediff["partIndex"])) == part {
			installed = true
		}
	}
	if !installed {
		return fmt.Errorf("target-after-complete: expected a native PegLeg hediff on the exact part, got %#v", afterCompleteRow)
	}

	// Replay: the exact same attempt returns an identical receipt, and does
	// not attempt to queue a second native bill.
	replayReply, err := h.Wire(ctx, "replay", "operations_execute", queueRequest)
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

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}

// failureCode wires request through operations_execute and returns the
// failure code, mirroring questfulfillaccept's/movementaccept's helper of the
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

// careToWire converts the native MedicalCareCategory.ToString() this
// acceptance binary observes through settings.medicalCare into the wire
// MedicalCare enum's ProtoJSON name, mirroring
// buildingruntime.surgeryCareFromNative's native-string mapping (note
// RimWorld's own enum member is "NoMeds", not "NoMedicine").
func careToWire(native string) (string, error) {
	switch native {
	case "NoCare":
		return "MEDICAL_CARE_NO_CARE", nil
	case "NoMeds":
		return "MEDICAL_CARE_NO_MEDICINE", nil
	case "HerbalOrWorse":
		return "MEDICAL_CARE_HERBAL_OR_WORSE", nil
	case "NormalOrWorse":
		return "MEDICAL_CARE_NORMAL_OR_WORSE", nil
	case "Best":
		return "MEDICAL_CARE_BEST", nil
	default:
		return "", fmt.Errorf("careToWire: unrecognized native medical care %q", native)
	}
}
