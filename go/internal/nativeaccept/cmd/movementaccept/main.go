// Command movementaccept replaces scripts/native_movement_acceptance.py: real
// ordinary typed pawn arrival through the scenario clock (no injected movement or
// completion), exact CAS/replay, no-op, and Manual/player override handling.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"sort"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-movement-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 20*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-movement-acceptance"
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
	report := na.NewReport("Real ordinary native Goto arrival under typed authority/clock, exact CAS/replay, "+
		"no-op, Manual and player override. No teleport or completion injection.", !*rendered)
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
	gabsExecutable, err := na.GABSExecutable(root, cfg.Configuration)
	if err != nil {
		return err
	}
	client, err := na.OpenSession(ctx, gabsExecutable, cfg.Configuration, gameID, 60*time.Second)
	if err != nil {
		return err
	}
	defer func() {
		stopCtx, stopCancel := context.WithTimeout(context.Background(), 60*time.Second)
		defer stopCancel()
		if stopped, err := client.GamesStop(stopCtx); err == nil {
			report["stop"] = string(stopped.Envelope)
		} else {
			report["stop_error"] = err.Error()
		}
		_ = client.Close()
	}()
	h := na.NewHarness(client, output)

	if _, err := h.Call(ctx, "new-game", "rimworld/start_debug_game_ready", map[string]any{
		"readiness": "visual", "pauseIfNeeded": true, "timeoutMs": 120000,
	}); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	identityBeforeReply, err := h.Wire(ctx, "identity-before", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, initial, err := na.Outcome(identityBeforeReply, "loaded")
	if err != nil {
		return err
	}
	if paused, _ := na.AsBool(initial["paused"]); !paused {
		return fmt.Errorf("fresh debug game did not start paused")
	}
	initialContext, _ := na.AsMap(initial["context"])
	identity, _ := na.AsMap(initialContext["identity"])

	read := func(label, pawnID string) (map[string]any, error) {
		filter := map[string]any{"colonist": true, "downed": false}
		if pawnID != "" {
			filter["ids"] = []any{pawnID}
		}
		reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "filter": filter,
		})
		if err != nil {
			return nil, err
		}
		return na.PawnRow(reply, identity, pawnID)
	}

	acquire := func(label string, leaseMs int) (map[string]any, error) {
		statusReply, err := h.Wire(ctx, label+"-status", "authority_read_status", map[string]any{"identity": identity})
		if err != nil {
			return nil, err
		}
		_, status, err := na.Outcome(statusReply, "status")
		if err != nil {
			return nil, err
		}
		statusContext, _ := na.AsMap(status["context"])
		grantReply, err := h.Wire(ctx, label, "authority_control", map[string]any{"acquire": map[string]any{
			"identity": identity, "expectedGeneration": statusContext["nativeGeneration"], "owner": na.Owner, "leaseMs": leaseMs,
		}})
		if err != nil {
			return nil, err
		}
		_, grant, err := na.Outcome(grantReply, "granted")
		return grant, err
	}

	revoke := func(label string, grant map[string]any) error {
		grantContext, _ := na.AsMap(grant["context"])
		reply, err := h.Wire(ctx, label, "authority_control", map[string]any{"revoke": map[string]any{
			"identity": identity, "expectedGeneration": grantContext["nativeGeneration"], "reason": "REVOCATION_REASON_MANUAL",
		}})
		if err != nil {
			return err
		}
		_, _, err = na.Outcome(reply, "revoked")
		return err
	}

	before, err := read("initial-pawn", "")
	if err != nil {
		return err
	}
	if drafted, _ := before["drafted"].(bool); drafted {
		return fmt.Errorf("initial pawn is unexpectedly already drafted")
	}
	if !na.DeepEqual(before["draftClaim"], map[string]any{"unowned": map[string]any{}}) {
		return fmt.Errorf("initial pawn does not have an unowned draft claim")
	}
	pawn, _ := na.AsMap(before["pawn"])
	pawnID := na.AsString(pawn["id"])

	grant, err := acquire("acquire", 30000)
	if err != nil {
		return err
	}
	draftReply, err := h.Wire(ctx, "draft", "operations_execute", na.ExecuteRequest(identity, grant, before, 1))
	if err != nil {
		return err
	}
	_, draft, err := na.Outcome(draftReply, "receipt")
	if err != nil {
		return err
	}
	owned, err := read("owned", pawnID)
	if err != nil {
		return err
	}
	if err := na.OwnedEffect(draft, owned, "applied", true); err != nil {
		return err
	}
	ownedPawn, _ := na.AsMap(owned["pawn"])
	origin, _ := na.AsMap(ownedPawn["position"])
	originX, originZ := na.AsNumber(origin["x"]), na.AsNumber(origin["z"])

	var exactCells []any
	for dx := -5; dx <= 5; dx++ {
		for dz := -5; dz <= 5; dz++ {
			distance := dx*dx + dz*dz
			if distance < 4 || distance > 25 {
				continue
			}
			x, z := originX+float64(dx), originZ+float64(dz)
			if x < 0 || z < 0 {
				continue
			}
			exactCells = append(exactCells, map[string]any{"x": x, "z": z})
		}
	}
	fields := map[string]any{
		"terrain": true, "roof": false, "visibility": true, "traversal": true,
		"zone": false, "areas": false, "things": false, "designations": false, "room": false, "growth": false,
	}
	cellsReply, err := h.Wire(ctx, "candidate-cells", "observations_get_cells", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "exactCells": map[string]any{"cells": exactCells}, "fields": fields,
	})
	if err != nil {
		return err
	}
	_, observedCells, err := na.Outcome(cellsReply, "observed")
	if err != nil {
		return err
	}
	if !na.DeepEqual(dig(observedCells, "context", "identity"), identity) {
		return fmt.Errorf("candidate-cells: context identity mismatch")
	}
	candidateCells, err := candidates(observedCells, origin)
	if err != nil {
		return err
	}

	var destination map[string]any
	limit := len(candidateCells)
	if limit > 24 {
		limit = 24
	}
	for number, cell := range candidateCells[:limit] {
		previewReply, err := h.Wire(ctx, fmt.Sprintf("preview-%d", number), "operations_preview", map[string]any{
			"identity": identity, "operation": map[string]any{"movePawn": map[string]any{"pawn": na.Target(owned), "destination": cell}},
		})
		if err != nil {
			return err
		}
		evaluated, hasEvaluated := na.AsMap(previewReply["evaluated"])
		if accepted, _ := na.AsBool(evaluated["accepted"]); hasEvaluated && accepted {
			destination = cell
			break
		}
		if _, isFailure := previewReply["failure"]; !isFailure {
			if !hasEvaluated {
				return fmt.Errorf("preview-%d: unexpected reply shape %#v", number, previewReply)
			}
			if accepted, ok := na.AsBool(evaluated["accepted"]); ok && accepted {
				return fmt.Errorf("preview-%d: accepted destination was not selected", number)
			}
		}
	}
	if destination == nil {
		return fmt.Errorf("no exact nearby normally reachable destination")
	}

	request := moveRequest(identity, grant, owned, 2, destination)
	receiptReply, err := h.Wire(ctx, "move", "operations_execute", request)
	if err != nil {
		return err
	}
	_, receipt, err := na.Outcome(receiptReply, "receipt")
	if err != nil {
		return err
	}
	moving, err := read("moving", pawnID)
	if err != nil {
		return err
	}
	issued, err := jobEffect(receipt, moving, destination)
	if err != nil {
		return err
	}
	precondition, _ := na.AsMap(request["precondition"])
	attempt := map[string]any{"identity": identity, "attempt": precondition["attempt"]}
	pendingReply, err := h.Wire(ctx, "pending", "receipts_observe_progress", attempt)
	if err != nil {
		return err
	}
	_, pending, err := na.Outcome(pendingReply, "progress")
	if err != nil {
		return err
	}
	if complete, _ := na.AsBool(pending["completeInspection"]); !complete {
		return fmt.Errorf("pending: progress read is not a complete inspection")
	}
	if _, ok := pending["pending"]; !ok {
		return fmt.Errorf("pending: expected a pending case, got %#v", pending)
	}

	replayReply, err := h.Wire(ctx, "replay", "operations_execute", request)
	if err != nil {
		return err
	}
	_, replay, err := na.Outcome(replayReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, receipt) {
		return fmt.Errorf("replay of the same attempt returned a different receipt")
	}
	replayRow, err := read("replay-unchanged", pawnID)
	if err != nil {
		return err
	}
	if err := na.SameControl(moving, replayRow); err != nil {
		return fmt.Errorf("replay: %w", err)
	}
	movingJob, _ := na.AsMap(moving["job"])
	replayJob, _ := na.AsMap(replayRow["job"])
	if !na.DeepEqual(movingJob, replayJob) {
		return fmt.Errorf("replay: job changed unexpectedly")
	}

	if code, err := failureCode(ctx, h, "stale-snapshot", moveRequest(identity, grant, owned, 3, origin)); err != nil {
		return err
	} else if code != "FAILURE_CODE_OWNER_CONFLICT" {
		return fmt.Errorf("stale-snapshot: expected FAILURE_CODE_OWNER_CONFLICT, got %q", code)
	}
	outsideMap := moveRequest(identity, grant, moving, 4, map[string]any{"x": float64(-1), "z": float64(-1)})
	if code, err := failureCode(ctx, h, "outside-map", outsideMap); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("outside-map: expected FAILURE_CODE_INVALID_REQUEST, got %q", code)
	}

	supervisor := &na.ScenarioClock{
		Wire: func(ctx context.Context, label, method string, request map[string]any) (map[string]any, error) {
			return h.Wire(ctx, label, method, request)
		},
		Identity: identity, Owner: na.AsString(na.Owner["controllerSessionId"]), Report: report, Grant: grant,
	}
	if err := supervisor.RenewAuthority(ctx); err != nil {
		return err
	}
	rt := &na.ScenarioRuntime{Query: h.Call, Clock: supervisor, Report: report}
	if _, err := na.AdvanceGame(ctx, rt, 240, na.WithTimeout(180*time.Second)); err != nil {
		return err
	}
	grant = supervisor.Grant
	if err := na.WaitForNativeTool(ctx, client, "test/b04f_setup", 30*time.Second); err != nil {
		if names, discErr := h.Discovery(ctx); discErr == nil {
			report["post_clock_discovery"] = names
		} else {
			report["post_clock_discovery_error"] = discErr.Error()
		}
		return fmt.Errorf("fixture tool did not become discoverable after the scenario clock window: %w", err)
	}

	arrived, err := read("arrived", pawnID)
	if err != nil {
		return err
	}
	completedReply, err := h.Wire(ctx, "completed", "receipts_observe_progress", attempt)
	if err != nil {
		return err
	}
	_, completed, err := na.Outcome(completedReply, "progress")
	if err != nil {
		return err
	}
	if err := arrival(completed, arrived, destination, issued); err != nil {
		return err
	}
	arrivedPawn, _ := na.AsMap(arrived["pawn"])
	arrivedSnapshot, _ := na.AsMap(arrivedPawn["snapshot"])
	arrivedContext, _ := na.AsMap(arrivedSnapshot["context"])
	if !na.DeepEqual(arrivedContext["nativeGeneration"], dig(receipt, "admittedContext", "nativeGeneration")) {
		return fmt.Errorf("arrived snapshot nativeGeneration does not match the move receipt")
	}

	noOpReply, err := h.Wire(ctx, "same-position", "operations_execute", moveRequest(identity, grant, arrived, 5, destination))
	if err != nil {
		return err
	}
	_, noOp, err := na.Outcome(noOpReply, "receipt")
	if err != nil {
		return err
	}
	noChange, _ := na.AsMap(noOp["noChange"])
	noChangeObserved, _ := na.AsMap(noChange["observed"])
	noChangeEffect, _ := na.AsMap(noChangeObserved["job"])
	if issued, _ := na.AsBool(noChangeEffect["issued"]); issued {
		return fmt.Errorf("same-position: expected issued=false")
	}
	if verified, _ := na.AsBool(noChangeEffect["verified"]); !verified {
		return fmt.Errorf("same-position: expected verified=true")
	}
	if _, present := noChangeEffect["jobId"]; present {
		return fmt.Errorf("same-position: unexpected jobId on a no-change effect")
	}
	noOpUnchanged, err := read("no-op-unchanged", pawnID)
	if err != nil {
		return err
	}
	if err := na.SameControl(arrived, noOpUnchanged); err != nil {
		return fmt.Errorf("no-op-unchanged: %w", err)
	}

	secondRequest := moveRequest(identity, grant, arrived, 6, origin)
	returnOrderReply, err := h.Wire(ctx, "return-order", "operations_execute", secondRequest)
	if err != nil {
		return err
	}
	if _, _, err := na.Outcome(returnOrderReply, "receipt"); err != nil {
		return fmt.Errorf("return-order: %w", err)
	}
	pendingReturn, err := read("before-player-order", pawnID)
	if err != nil {
		return err
	}
	external, err := h.Call(ctx, "external-order", "test/b04f_setup", map[string]any{"op": "external-order", "pawn": pawnID})
	if err != nil {
		return err
	}
	overridden, err := read("after-player-order", pawnID)
	if err != nil {
		return err
	}
	if err := na.ActualOrder(external, overridden); err != nil {
		return err
	}
	if !na.DeepEqual(overridden["draftClaim"], map[string]any{"unowned": map[string]any{}}) {
		return fmt.Errorf("player order did not clear the draft claim")
	}
	if na.DeepEqual(na.Target(overridden), na.Target(pendingReturn)) {
		return fmt.Errorf("player order did not rotate the snapshot token")
	}
	secondPrecondition, _ := na.AsMap(secondRequest["precondition"])
	interruptedReply, err := h.Wire(ctx, "interrupted", "receipts_observe_progress", map[string]any{
		"identity": identity, "attempt": secondPrecondition["attempt"],
	})
	if err != nil {
		return err
	}
	_, interrupted, err := na.Outcome(interruptedReply, "progress")
	if err != nil {
		return err
	}
	unsuccessful, _ := na.AsMap(interrupted["unsuccessful"])
	if na.AsString(unsuccessful["reason"]) != "UNSUCCESSFUL_REASON_INTERRUPTED" {
		return fmt.Errorf("interrupted: expected UNSUCCESSFUL_REASON_INTERRUPTED, got %#v", interrupted)
	}
	grant, err = acquire("unowned-acquire", 30000)
	if err != nil {
		return err
	}
	if code, err := failureCode(ctx, h, "unowned-refusal", moveRequest(identity, grant, overridden, 7, origin)); err != nil {
		return err
	} else if code != "FAILURE_CODE_OWNER_CONFLICT" {
		return fmt.Errorf("unowned-refusal: expected FAILURE_CODE_OWNER_CONFLICT, got %q", code)
	}
	overridePreserved, err := read("override-preserved", pawnID)
	if err != nil {
		return err
	}
	if err := na.SameControl(overridden, overridePreserved); err != nil {
		return fmt.Errorf("override-preserved: %w", err)
	}
	if err := revoke("manual", grant); err != nil {
		return err
	}
	manualRefusalReply, err := h.Wire(ctx, "manual-refusal", "operations_execute", moveRequest(identity, grant, overridden, 8, origin))
	if err != nil {
		return err
	}
	if _, _, err := na.Outcome(manualRefusalReply, "failure"); err != nil {
		return fmt.Errorf("manual-refusal: %w", err)
	}

	identityAfterReply, err := h.Wire(ctx, "identity-after", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, final, err := na.Outcome(identityAfterReply, "loaded")
	if err != nil {
		return err
	}
	if paused, _ := na.AsBool(final["paused"]); !paused {
		return fmt.Errorf("game unexpectedly resumed")
	}
	finalContext, _ := na.AsMap(final["context"])
	if !na.DeepEqual(finalContext["identity"], identity) {
		return fmt.Errorf("identity changed during the run")
	}
	ticks := na.AsNumber(finalContext["tick"]) - na.AsNumber(initialContext["tick"])
	if ticks != 240 {
		return fmt.Errorf("expected exactly 240 ticks to elapse, got %v", ticks)
	}
	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	if err := na.CheckStartupLog(string(logData), headless); err != nil {
		return err
	}
	report["pawn_id"] = pawnID
	report["destination"] = destination
	report["issued"] = issued
	report["final_context"] = finalContext
	return nil
}

// moveRequest builds an operations_execute movePawn request under grant's lease,
// mirroring native_movement_acceptance.py's move_request().
func moveRequest(identity, grant, row map[string]any, number int, destination map[string]any) map[string]any {
	request := na.ExecuteRequest(identity, grant, row, number)
	request["operation"] = map[string]any{"movePawn": map[string]any{"pawn": na.Target(row), "destination": destination}}
	return request
}

// candidates filters and orders an observations_get_cells reply's cells to the exact
// set of nearby, walkable, passable, unfogged candidates, mirroring
// native_movement_acceptance.py's candidates().
func candidates(snapshot, origin map[string]any) ([]map[string]any, error) {
	completeness, _ := na.AsMap(snapshot["completeness"])
	page, _ := na.AsMap(completeness["page"])
	if complete, _ := na.AsBool(page["complete"]); !complete || na.AsNumber(completeness["unreadable"]) != 0 {
		return nil, fmt.Errorf("candidate cells: incomplete or unreadable page: %#v", completeness)
	}
	rows := na.AsSlice(snapshot["cells"])
	matched, returned := na.AsNumber(completeness["matched"]), na.AsNumber(completeness["returned"])
	if matched != returned || int(returned) != len(rows) {
		return nil, fmt.Errorf("candidate cells: completeness count mismatch: %#v", completeness)
	}
	originX, originZ := na.AsNumber(origin["x"]), na.AsNumber(origin["z"])
	var result []map[string]any
	for _, raw := range rows {
		row, _ := na.AsMap(raw)
		cell, _ := na.AsMap(row["cell"])
		cx, cz := na.AsNumber(cell["x"]), na.AsNumber(cell["z"])
		distance := (cx-originX)*(cx-originX) + (cz-originZ)*(cz-originZ)
		walkable, _ := na.AsBool(row["walkable"])
		passable, _ := na.AsBool(row["passable"])
		fogged, _ := na.AsBool(row["fogged"])
		if distance >= 4 && distance <= 25 && walkable && passable && !fogged {
			if row["terrain"] == nil {
				return nil, fmt.Errorf("candidate cells: missing terrain for a walkable cell: %#v", row)
			}
			result = append(result, cell)
		}
	}
	sort.Slice(result, func(i, j int) bool {
		xi, zi := na.AsNumber(result[i]["x"]), na.AsNumber(result[i]["z"])
		xj, zj := na.AsNumber(result[j]["x"]), na.AsNumber(result[j]["z"])
		di := (xi-originX)*(xi-originX) + (zi-originZ)*(zi-originZ)
		dj := (xj-originX)*(xj-originX) + (zj-originZ)*(zj-originZ)
		if di != dj {
			return di < dj
		}
		if xi != xj {
			return xi < xj
		}
		return zi < zj
	})
	return result, nil
}

// jobEffect asserts an operations_execute receipt's applied job matches a fresh Goto
// order to destination, mirroring native_movement_acceptance.py's job_effect().
func jobEffect(receipt, row, destination map[string]any) (map[string]any, error) {
	applied, _ := na.AsMap(receipt["applied"])
	observed, _ := na.AsMap(applied["observed"])
	effect, _ := na.AsMap(observed["job"])
	issued, _ := na.AsBool(effect["issued"])
	verified, _ := na.AsBool(effect["verified"])
	if !issued || !verified {
		return nil, fmt.Errorf("job effect not issued/verified: %#v", effect)
	}
	pawn, _ := na.AsMap(row["pawn"])
	if na.AsString(effect["pawnId"]) != na.AsString(pawn["id"]) || na.AsString(effect["jobDef"]) != "Goto" {
		return nil, fmt.Errorf("job effect pawn/jobDef mismatch: %#v", effect)
	}
	targetA, _ := na.AsMap(effect["targetA"])
	if !na.DeepEqual(targetA["cell"], destination) {
		return nil, fmt.Errorf("job effect targetA cell does not match destination: %#v", effect)
	}
	job, _ := na.AsMap(row["job"])
	if na.AsString(job["loadId"]) != fmt.Sprint(effect["jobId"]) || na.AsString(job["defName"]) != "Goto" {
		return nil, fmt.Errorf("row job does not match the issued effect: %#v", job)
	}
	draftClaim, _ := na.AsMap(row["draftClaim"])
	owned, _ := na.AsMap(draftClaim["owned"])
	if na.AsString(effect["draftClaimId"]) != na.AsString(owned["claimId"]) {
		return nil, fmt.Errorf("job effect draftClaimId does not match row's owned claim")
	}
	return effect, nil
}

// arrival asserts row has physically arrived at destination and the observed progress
// records a causally verified completion matching issued, mirroring
// native_movement_acceptance.py's arrival().
func arrival(progress, row, destination, issued map[string]any) error {
	pawn, _ := na.AsMap(row["pawn"])
	if !na.DeepEqual(pawn["position"], destination) {
		return fmt.Errorf("pawn did not arrive at destination")
	}
	complete, _ := na.AsBool(progress["completeInspection"])
	completed, ok := na.AsMap(progress["completed"])
	if !complete || !ok {
		return fmt.Errorf("expected a complete arrival progress: %#v", progress)
	}
	evidence, _ := na.AsMap(completed["evidence"])
	effect, _ := na.AsMap(evidence["job"])
	if fmt.Sprint(effect["jobId"]) != fmt.Sprint(issued["jobId"]) {
		return fmt.Errorf("arrival jobId does not match the issued job")
	}
	targetA, _ := na.AsMap(effect["targetA"])
	if !na.DeepEqual(targetA["cell"], destination) {
		return fmt.Errorf("arrival targetA cell does not match destination")
	}
	draftClaim, _ := na.AsMap(row["draftClaim"])
	owned, _ := na.AsMap(draftClaim["owned"])
	if na.AsString(effect["draftClaimId"]) != na.AsString(issued["draftClaimId"]) || na.AsString(effect["draftClaimId"]) != na.AsString(owned["claimId"]) {
		return fmt.Errorf("arrival draftClaimId mismatch")
	}
	if verified, _ := na.AsBool(effect["verified"]); !verified {
		return fmt.Errorf("arrival job effect was not verified")
	}
	return nil
}

// failureCode wires request through operations_execute and returns the failure code
// of a refused reply, asserting the reply is in fact a failure.
func failureCode(ctx context.Context, h *na.Harness, label string, request map[string]any) (string, error) {
	reply, err := h.Wire(ctx, label, "operations_execute", request)
	if err != nil {
		return "", err
	}
	_, failure, err := na.Outcome(reply, "failure")
	if err != nil {
		return "", fmt.Errorf("%s: %w", label, err)
	}
	return na.AsString(failure["code"]), nil
}

func dig(m map[string]any, path ...string) any {
	var current any = m
	for _, key := range path {
		next, ok := na.AsMap(current)
		if !ok {
			return nil
		}
		current = next[key]
	}
	return current
}
