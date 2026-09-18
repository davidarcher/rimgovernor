// Command temperatureaccept exercises the SetBuildingTemperature vertical
// (G01.08) end to end against a live game: an existing player-owned building
// with a native CompTempControl, the exact rimgovernor/operations_execute
// PatchBuilding wire contract Go's buildingtemperature.Boundary drives, its
// CAS/stale-identity refusal, and its observation/replay semantics. Uses a
// private disposable fixture (test/building_temperature_prepare) since a
// fresh baseline colony does not reliably start with a temperature-
// controlled building.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const sessionOwner = "native-building-temperature-acceptance"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-building-temperature-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 20*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-building-temperature-acceptance"
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
	report := na.NewReport("Native SetBuildingTemperature vertical: PatchBuilding target-temperature CAS "+
		"admission against an exact CompTempControl building, stale-identity refusal, replay idempotency "+
		"and durable lookup.", !*rendered)
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
	s, err := na.OpenSession(ctx, cfg, report, na.Fixture{Op: "test/building_temperature_prepare"}, na.QuietRequired)
	if err != nil {
		return err
	}
	defer s.Close()
	h, identity, prepared := s.Harness, s.Identity, s.Prepared
	if !na.Contains(s.Names, "rimgovernor/observations_list_buildings") {
		return fmt.Errorf("missing rimgovernor/observations_list_buildings in discovery")
	}
	thingID := na.AsString(prepared["thingId"])
	if thingID == "" {
		return fmt.Errorf("building_temperature_prepare: unexpected fixture identifier: %#v", prepared)
	}

	// target mirrors bridge.ReadBuildingTemperatureTarget's own extraction
	// from rimgovernor/observations_list_buildings, so the tokens/facts used
	// below are exactly what the Go boundary itself would compute.
	target := func(label string) (row map[string]any, err error) {
		reply, err := h.Wire(ctx, label, "observations_list_buildings", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "ids": []string{thingID},
		})
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, err
		}
		buildings := na.AsSlice(observed["buildings"])
		if len(buildings) != 1 {
			return nil, fmt.Errorf("%s: expected exactly one building, got %#v", label, observed)
		}
		row, _ = na.AsMap(buildings[0])
		building, _ := na.AsMap(row["building"])
		if na.AsString(building["id"]) != thingID {
			return nil, fmt.Errorf("%s: fixture building identity mismatch: %#v", label, row)
		}
		return row, nil
	}
	settingsOf := func(row map[string]any) (temperature float64, token string, err error) {
		settings, ok := na.AsMap(row["settings"])
		if !ok {
			return 0, "", fmt.Errorf("missing settings: %#v", row)
		}
		snapshot, ok := na.AsMap(settings["snapshot"])
		if !ok || na.AsString(snapshot["entityId"]) != thingID {
			return 0, "", fmt.Errorf("missing or mismatched settings snapshot: %#v", settings)
		}
		if _, present := settings["targetTemperatureC"]; !present {
			return 0, "", fmt.Errorf("missing targetTemperatureC: %#v", settings)
		}
		return na.AsNumber(settings["targetTemperatureC"]), na.AsString(snapshot["token"]), nil
	}

	beforeRow, err := target("target-before")
	if err != nil {
		return err
	}
	beforeTemperature, beforeToken, err := settingsOf(beforeRow)
	if err != nil {
		return err
	}
	if beforeToken == "" {
		return fmt.Errorf("target-before: missing settings snapshot token: %#v", beforeRow)
	}
	newTemperature := beforeTemperature + 5
	if newTemperature > 1000 {
		newTemperature = beforeTemperature - 5
	}

	grant, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity)
	if err != nil {
		return err
	}
	grantContext, _ := na.AsMap(grant["context"])

	buildOperation := func(temperature float64, token string) map[string]any {
		return map[string]any{"patchBuilding": map[string]any{
			"building":          map[string]any{"entityId": thingID, "expectedSnapshotToken": token},
			"targetTemperature": temperature,
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

	// Refusal 1: a deliberately stale token must be refused rather than
	// silently admitted, proving the CAS token is load-bearing.
	staleRequest := buildRequest("temperature-stale", "1", buildOperation(newTemperature, beforeToken+"-stale"))
	if code, err := failureCode(ctx, h, "stale-token", staleRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_STALE_IDENTITY" {
		return fmt.Errorf("stale-token: expected FAILURE_CODE_STALE_IDENTITY, got %q", code)
	}

	// Preview: accepted, but never mutates the live CompTempControl.
	previewReply, err := h.Wire(ctx, "preview", "operations_preview", map[string]any{
		"identity": identity, "operation": buildOperation(newTemperature, beforeToken),
	})
	if err != nil {
		return err
	}
	evaluated, ok := na.AsMap(previewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview: expected an evaluated reply, got %#v", previewReply)
	}
	if accepted, _ := na.AsBool(evaluated["accepted"]); !accepted {
		return fmt.Errorf("preview: expected the temperature patch to be accepted, got %#v", evaluated)
	}
	afterPreviewRow, err := target("target-after-preview")
	if err != nil {
		return err
	}
	afterPreviewTemperature, afterPreviewToken, err := settingsOf(afterPreviewRow)
	if err != nil {
		return err
	}
	if afterPreviewTemperature != beforeTemperature || afterPreviewToken != beforeToken {
		return fmt.Errorf("target-after-preview: expected the dry-run preview to leave the building unchanged")
	}

	// Execute: the real native CompTempControl.targetTemperature write.
	executeRequest := buildRequest("temperature-set", "1", buildOperation(newTemperature, beforeToken))
	receiptReply, err := h.Wire(ctx, "execute", "operations_execute", executeRequest)
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
	appliedSettings, _ := na.AsMap(appliedObserved["settings"])
	appliedSnapshot, _ := na.AsMap(appliedSettings["snapshot"])
	if na.AsString(appliedSnapshot["entityId"]) != thingID || na.AsString(appliedSnapshot["beforeToken"]) != beforeToken {
		return fmt.Errorf("execute: unexpected applied snapshot: %#v", appliedSnapshot)
	}
	afterToken := na.AsString(appliedSnapshot["afterToken"])
	if afterToken == "" || afterToken == beforeToken {
		return fmt.Errorf("execute: expected the snapshot token to change, got %#v", appliedSnapshot)
	}
	fields := na.AsSlice(appliedSettings["fields"])
	if len(fields) != 1 {
		return fmt.Errorf("execute: expected exactly one field result, got %#v", appliedSettings)
	}
	field, _ := na.AsMap(fields[0])
	if na.AsString(field["field"]) != "SETTINGS_FIELD_TEMPERATURE" || na.AsString(field["outcome"]) != "FIELD_OUTCOME_APPLIED" {
		return fmt.Errorf("execute: unexpected field result: %#v", field)
	}

	afterExecuteRow, err := target("target-after-execute")
	if err != nil {
		return err
	}
	afterExecuteTemperature, afterExecuteToken, err := settingsOf(afterExecuteRow)
	if err != nil {
		return err
	}
	if afterExecuteToken != afterToken {
		return fmt.Errorf("target-after-execute: expected the fresh read token to match the receipt's afterToken")
	}
	if diff := afterExecuteTemperature - newTemperature; diff > 0.01 || diff < -0.01 {
		return fmt.Errorf("target-after-execute: expected the native target temperature to change to %v, got %v", newTemperature, afterExecuteTemperature)
	}

	// Observe: durable progress lookup reports the same completed evidence.
	precondition, _ := na.AsMap(executeRequest["precondition"])
	attempt := map[string]any{"identity": identity, "attempt": precondition["attempt"]}
	progressReply, err := h.Wire(ctx, "observe", "receipts_observe_progress", attempt)
	if err != nil {
		return err
	}
	_, progress, err := na.Outcome(progressReply, "progress")
	if err != nil {
		return err
	}
	if complete, _ := na.AsBool(progress["completeInspection"]); !complete {
		return fmt.Errorf("observe: expected completeInspection=true, got %#v", progress)
	}
	completed, ok := na.AsMap(progress["completed"])
	if !ok {
		return fmt.Errorf("observe: expected a completed outcome, got %#v", progress)
	}
	completedEvidence, _ := na.AsMap(completed["evidence"])
	completedSettings, _ := na.AsMap(completedEvidence["settings"])
	completedSnapshot, _ := na.AsMap(completedSettings["snapshot"])
	if na.AsString(completedSnapshot["entityId"]) != thingID || na.AsString(completedSnapshot["afterToken"]) != afterToken {
		return fmt.Errorf("observe: unexpected completed evidence: %#v", completedSettings)
	}

	// Replay: the exact same attempt returns an identical receipt, and does
	// not attempt the native write a second time.
	replayReply, err := h.Wire(ctx, "replay", "operations_execute", executeRequest)
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

	// Refusal 2: a brand-new attempt against the now-stale before-token is
	// refused rather than silently re-admitted or double-applied.
	postExecuteRequest := buildRequest("temperature-set-again", "1", buildOperation(beforeTemperature, beforeToken))
	if code, err := failureCode(ctx, h, "post-execute-retry", postExecuteRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_STALE_IDENTITY" {
		return fmt.Errorf("post-execute-retry: expected FAILURE_CODE_STALE_IDENTITY, got %q", code)
	}

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}

// failureCode wires request through operations_execute and returns the
// failure code, mirroring questfulfillaccept's/the movement case's helper of
// the same name.
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
