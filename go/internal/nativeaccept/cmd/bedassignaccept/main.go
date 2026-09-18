// Command bedassignaccept exercises the typed AssignBed native dispatch
// vertical (N01.04, issue #34): NativeBedAssignOperations' Execute handler
// (integrations/rimgovernor-native/src/Bridge/Protocol/
// NativeBedAssignOperations.cs), the typed adapter that drives the same
// CompAssignableToPawn.TryAssignPawn write the legacy JSON home/upkeep_bed
// tool (UpkeepBedTool.cs) used, through the typed operations contract
// instead. A colonist who genuinely owns one bed actually has their bed
// ownership reassigned to a different, real, previously-unclaimed compliant
// bed by a real native execute, observed via a real
// rimgovernor/observations_list_pawns readback (not just a receipt), not a
// job/tick-driven effect: CompAssignableToPawn.TryAssignPawn is synchronous,
// so unlike recoveryserviceaccept this tool does not poll game ticks to
// completion.
//
// Uses the disposable test/bed_assign_prepare fixture (BedAssignFixture.cs)
// since a deterministic pre-owned bed and a second, unclaimed, compliant
// target bed cannot be relied on from native random colony bed layout,
// mirroring recoveryareaaccept's own fixture-first pattern.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const sessionOwner = "native-bed-assign-acceptance"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-bed-assign-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 30*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-bed-assign-acceptance"
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
	report := na.NewReport("Native CompAssignableToPawn.TryAssignPawn (typed AssignBed) dispatch: a colonist who "+
		"genuinely owns one bed actually has their bed ownership reassigned to a different real, previously-"+
		"unclaimed compliant bed by a real native execute issued through the typed operations contract, exact "+
		"CAS/stale-identity refusal, preview non-mutation, real bed-ownership change observed via native readback "+
		"(not just a receipt), and replay idempotency.", !*rendered)
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
	// Fixture: one colonist who already owns a bed (the known "previous
	// bed" AssignBed guards against) and one roofed, unclaimed, compliant
	// target bed. It runs BEFORE acquiring authority: the fixture edits
	// pawn/building state directly outside any authority.Owned() scope,
	// and NativeControlAuthority.RevokeExternal revokes any held lease for
	// such external activity regardless of who holds it, mirroring
	// recoveryareaaccept's own ordering.
	s, err := na.OpenSession(ctx, cfg, report, na.Fixture{Op: "test/bed_assign_prepare"}, na.QuietRequired)
	if err != nil {
		return err
	}
	defer s.Close()
	h, identity, names, prepared := s.Harness, s.Identity, s.Names, s.Prepared
	if !na.Contains(names, "rimgovernor/operations_execute") {
		return fmt.Errorf("missing rimgovernor/operations_execute in discovery")
	}
	if !na.Contains(names, "rimgovernor/observations_list_pawns") {
		return fmt.Errorf("missing rimgovernor/observations_list_pawns in discovery")
	}
	if !na.Contains(names, "rimgovernor/observations_list_buildings") {
		return fmt.Errorf("missing rimgovernor/observations_list_buildings in discovery")
	}

	pawnID := na.AsString(prepared["pawn"])
	previousBedID := na.AsString(prepared["previousBed"])
	bedID := na.AsString(prepared["bed"])
	if pawnID == "" || previousBedID == "" || bedID == "" {
		return fmt.Errorf("prepare: missing fixture pawn/previousBed/bed ids: %#v", prepared)
	}
	report["fixture_pawn"] = pawnID
	report["fixture_previous_bed"] = previousBedID
	report["fixture_bed"] = bedID

	// acquire takes a fresh authority lease: the explicit player-control
	// takeover path. Only one dispatch happens in this tool (no Fast-speed
	// tick-advance window precedes it, since TryAssignPawn is synchronous),
	// so a single acquire before the whole stale-token/preview/execute
	// sequence is enough.
	acquire := func(label string) error {
		_, err := na.GrantAuto(ctx, h.WireFunc(), label, identity)
		return err
	}
	if err := acquire("acquire"); err != nil {
		return err
	}

	currentGeneration := func(label string) (any, error) {
		reply, err := h.Wire(ctx, label, "authority_read_status", map[string]any{"identity": identity})
		if err != nil {
			return nil, err
		}
		_, status, err := na.Outcome(reply, "status")
		if err != nil {
			return nil, err
		}
		statusContext, _ := na.AsMap(status["context"])
		return statusContext["nativeGeneration"], nil
	}

	// pawnState reads the exact native pawn-state snapshot token and the
	// pawn's current ownedBedId through rimgovernor/observations_list_pawns,
	// the same call bridge.ReadBedAssignPawn's own boundary read issues.
	pawnState := func(label string) (token, ownedBedID string, err error) {
		reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{
			"scope":   map[string]any{"expectedIdentity": identity},
			"filter":  map[string]any{"ids": []string{pawnID}},
			"details": map[string]any{},
			"page":    map[string]any{"limit": 1},
		})
		if err != nil {
			return "", "", err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return "", "", err
		}
		rows := na.AsSlice(observed["pawns"])
		if len(rows) != 1 {
			return "", "", fmt.Errorf("%s: expected exactly one observed pawn, got %#v", label, observed)
		}
		row, _ := na.AsMap(rows[0])
		pawn, _ := na.AsMap(row["pawn"])
		if na.AsString(pawn["id"]) != pawnID {
			return "", "", fmt.Errorf("%s: unexpected pawn row: %#v", label, row)
		}
		snapshot, _ := na.AsMap(row["snapshot"])
		token = na.AsString(snapshot["token"])
		if token == "" {
			return "", "", fmt.Errorf("%s: missing pawn snapshot token: %#v", label, row)
		}
		ownedBedID = na.AsString(row["ownedBedId"])
		return token, ownedBedID, nil
	}

	// bedState reads the exact native building snapshot token through
	// rimgovernor/observations_list_buildings, the same generic building CAS
	// token bridge.ReadBedTarget reuses (a bed is a player building like
	// any repaired structure).
	bedState := func(label, id string) (token string, err error) {
		reply, err := h.Wire(ctx, label, "observations_list_buildings", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "ids": []string{id},
		})
		if err != nil {
			return "", err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return "", err
		}
		buildings := na.AsSlice(observed["buildings"])
		if len(buildings) != 1 {
			return "", fmt.Errorf("%s: expected exactly one building, got %#v", label, observed)
		}
		row, _ := na.AsMap(buildings[0])
		building, _ := na.AsMap(row["building"])
		if na.AsString(building["id"]) != id {
			return "", fmt.Errorf("%s: unexpected building row: %#v", label, row)
		}
		snapshot, _ := na.AsMap(row["snapshot"])
		token = na.AsString(snapshot["token"])
		if token == "" {
			return "", fmt.Errorf("%s: missing building snapshot token: %#v", label, row)
		}
		return token, nil
	}

	// buildOperation mirrors bridge.bedAssignCommand
	// (go/internal/bridge/bed_assign.go): an AssignBed naming the pawn, the
	// target bed and the expected previous bed.
	buildOperation := func(pID, pToken, bID, bToken, prevBedID string) map[string]any {
		return map[string]any{"assignBed": map[string]any{
			"pawn":                map[string]any{"entityId": pID, "expectedSnapshotToken": pToken},
			"bed":                 map[string]any{"entityId": bID, "expectedSnapshotToken": bToken},
			"expectedPreviousBed": map[string]any{"entityId": prevBedID},
		}}
	}
	buildRequest := func(actionID string, generation any, operation map[string]any) map[string]any {
		return map[string]any{
			"precondition": map[string]any{
				"identity": identity, "expectedGeneration": generation,
				"attempt": map[string]any{"controllerSessionId": sessionOwner, "actionId": actionID, "attemptId": "1"},
			},
			"operation": operation,
		}
	}
	failureCode := func(label string, request map[string]any) (string, error) {
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

	beforePawnToken, beforeOwnedBed, err := pawnState("pawn-before")
	if err != nil {
		return err
	}
	if beforeOwnedBed != previousBedID {
		return fmt.Errorf("pawn-before: expected the fixture colonist to own %q, got %q", previousBedID, beforeOwnedBed)
	}
	beforeBedToken, err := bedState("bed-before", bedID)
	if err != nil {
		return err
	}
	report["pawn_before_owned_bed"] = beforeOwnedBed

	// Refusal: a stale performer CAS token must be refused.
	staleGeneration, err := currentGeneration("generation-stale")
	if err != nil {
		return err
	}
	staleRequest := buildRequest("bed-stale-token", staleGeneration,
		buildOperation(pawnID, "stale-pawn-token-00000000000000000000000000000000", bedID, beforeBedToken, previousBedID))
	if code, err := failureCode("stale-token", staleRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_OWNER_CONFLICT" && code != "FAILURE_CODE_NOT_FOUND" && code != "FAILURE_CODE_STALE_IDENTITY" && code != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("stale-token: expected a stale-identity/owner-conflict/invalid-request refusal, got %q", code)
	}

	// Preview: accepted, but never mutates the live bed ownership.
	assignOperation := buildOperation(pawnID, beforePawnToken, bedID, beforeBedToken, previousBedID)
	previewReply, err := h.Wire(ctx, "preview-bed", "operations_preview", map[string]any{"identity": identity, "operation": assignOperation})
	if err != nil {
		return err
	}
	previewEvaluated, ok := na.AsMap(previewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview-bed: expected an evaluated reply, got %#v", previewReply)
	}
	if accepted, _ := na.AsBool(previewEvaluated["accepted"]); !accepted {
		return fmt.Errorf("preview-bed: expected the bed assignment to be accepted, got %#v", previewEvaluated)
	}
	_, afterPreviewOwnedBed, err := pawnState("pawn-after-preview")
	if err != nil {
		return err
	}
	if afterPreviewOwnedBed != previousBedID {
		return fmt.Errorf("pawn-after-preview: expected an unchanged owned bed from a dry-run preview, got %q (was %q)", afterPreviewOwnedBed, previousBedID)
	}

	// Execute: the real native CompAssignableToPawn.TryAssignPawn dispatch.
	// Re-read the current nativeGeneration immediately before dispatch: it
	// advances over wall-clock/tick time independent of this tool's own
	// writes.
	executeGeneration, err := currentGeneration("generation-before-execute")
	if err != nil {
		return err
	}
	executeRequest := buildRequest("bed-execute", executeGeneration, assignOperation)
	executeReply, err := h.Wire(ctx, "execute-bed", "operations_execute", executeRequest)
	if err != nil {
		return err
	}
	_, executeReceipt, err := na.Outcome(executeReply, "receipt")
	if err != nil {
		return err
	}
	executeApplied, ok := na.AsMap(executeReceipt["applied"])
	if !ok {
		return fmt.Errorf("execute-bed: expected an applied outcome, got %#v", executeReceipt)
	}
	executeObserved, _ := na.AsMap(executeApplied["observed"])
	executeBed, ok := na.AsMap(executeObserved["bed"])
	if !ok {
		return fmt.Errorf("execute-bed: expected bed effect evidence, got %#v", executeObserved)
	}
	if na.AsString(executeBed["pawnId"]) != pawnID || na.AsString(executeBed["bedId"]) != bedID ||
		na.AsString(executeBed["previousBedId"]) != previousBedID {
		return fmt.Errorf("execute-bed: unexpected bed effect identity: %#v", executeBed)
	}
	if assigned, _ := na.AsBool(executeBed["assigned"]); !assigned {
		return fmt.Errorf("execute-bed: expected assigned=true in the effect evidence, got %#v", executeBed)
	}

	// Observe: the native write is synchronous (an assignable-comp claim,
	// not a job), so the readback is issued immediately -- no Fast-speed
	// tick-advance window is needed, unlike a job-driven dispatch such as
	// recoveryserviceaccept's own repair job.
	_, afterOwnedBed, err := pawnState("pawn-after-execute")
	if err != nil {
		return err
	}
	if afterOwnedBed != bedID {
		return fmt.Errorf("pawn-after-execute: expected the native colonist to now own %q, got %q", bedID, afterOwnedBed)
	}
	report["pawn_after_owned_bed"] = afterOwnedBed
	report["bed_reassigned"] = true

	// Replay: the exact same attempt returns an identical receipt.
	replayReply, err := h.Wire(ctx, "replay-bed", "operations_execute", executeRequest)
	if err != nil {
		return err
	}
	_, replay, err := na.Outcome(replayReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, executeReceipt) {
		return fmt.Errorf("replay-bed: replay of the same attempt returned a different receipt")
	}
	executePrecondition, _ := na.AsMap(executeRequest["precondition"])
	executeAttempt := map[string]any{"identity": identity, "attempt": executePrecondition["attempt"]}
	lookupReply, err := h.Wire(ctx, "lookup-bed", "receipts_lookup", executeAttempt)
	if err != nil {
		return err
	}
	_, lookup, err := na.Outcome(lookupReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(lookup, executeReceipt) {
		return fmt.Errorf("lookup-bed: expected the same receipt as execute, got %#v", lookup)
	}

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}
