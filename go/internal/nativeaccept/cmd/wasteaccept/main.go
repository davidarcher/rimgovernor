// Command wasteaccept exercises the native waste containment vertical
// (MaintainWaste, issue #27): NativeWasteOperations' ManageWaste operation
// (integrations/rimgovernor-native/src/Bridge/Protocol/NativeWasteOperations.cs),
// wired onto Operation_ManageWaste in NativeOperationTools.cs's Execute/Preview
// dispatch. Unlike every other closed vertical this session, waste had no live
// acceptance harness at all -- only unit-level candidate/monitoring coverage.
//
// A genuinely unwanted item (a disposable WoodLog) is actually hauled by a
// real native hauling WorkGiver job, issued by an already-selected undrafted
// colonist, into a real player-designated dirty outdoor stockpile, observed
// via real game ticks and independently confirmed by re-reading the exact
// cells before and after (rimgovernor/observations_get_cells with things
// requested) -- not just a receipt.
//
// This tool also exercises the real fix for a native gap this session found:
// NativeObservationTools.GetCells previously refused any request for the
// "things" cell field as Common.FailureCode.Unsupported, which meant
// bridge.ReadWasteTarget (the only production path that can refresh a waste
// item's CAS token; there is no per-item lookup RPC) could never actually
// succeed against a real running game. NativeObservationTools now populates
// each requested cell's things, with each entity's own CAS token computed by
// the same NativeWasteOperations.Token(...) hash NativeWasteOperations.Prepare
// checks -- proven live here, not just by a receipt.
//
// Uses the disposable test/waste_fixture fixture (scripts/fixtures/
// WasteFixture.cs): one hauling-capable colonist, one rotten
// anonymous corpse, one unwanted WoodLog, one forbidden (protected) Steel
// stack, and a player-designated dirty outdoor dumping stockpile -- mirroring
// every other vertical's fixture-first pattern since deterministic dirty/
// waste preconditions cannot be relied on from native random generation.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const sessionOwner = "native-waste-acceptance"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-waste-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 30*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-waste-acceptance"
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
	report := na.NewReport("Native ManageWaste (MaintainWaste) dispatch: a genuinely unwanted item is actually "+
		"hauled by a real native hauling WorkGiver job into a real player-designated dirty stockpile, exact "+
		"CAS/stale-identity refusal, forbidden-item protection refusal, preview non-mutation, real position "+
		"change observed via native ticks and independent cell re-reads (not just a receipt), and replay "+
		"idempotency.", !*rendered)
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
	// Fixture: one hauling colonist, one rotten anonymous corpse, one
	// unwanted WoodLog, one forbidden (protected) Steel stack, and a dirty
	// outdoor dumping stockpile. Run before acquiring authority, mirroring
	// recoveryserviceaccept's own ordering (NativeControlAuthority.
	// RevokeExternal revokes any held lease for such external activity).
	s, err := na.OpenSession(ctx, cfg, report, na.Fixture{Op: "test/waste_fixture", Args: map[string]any{"burial": false}}, na.QuietRequired)
	if err != nil {
		return err
	}
	defer s.Close()
	h, identity, prepared := s.Harness, s.Identity, s.Prepared
	if !na.Contains(s.Names, "rimgovernor/operations_execute") {
		return fmt.Errorf("missing rimgovernor/operations_execute in discovery")
	}
	pawnID := na.AsString(prepared["pawn"])
	corpseID := na.AsString(prepared["corpse"])
	unwantedID := na.AsString(prepared["unwanted"])
	protectedID := na.AsString(prepared["protectedItem"])
	if pawnID == "" || corpseID == "" || unwantedID == "" || protectedID == "" {
		return fmt.Errorf("prepare: missing fixture ids: %#v", prepared)
	}
	destination, _ := na.AsMap(prepared["destination"])
	unwantedCellRaw, _ := na.AsMap(prepared["unwantedCell"])
	protectedCellRaw, _ := na.AsMap(prepared["protectedCell"])
	unwantedCell := map[string]any{"x": unwantedCellRaw["x"], "z": unwantedCellRaw["z"]}
	protectedCell := map[string]any{"x": protectedCellRaw["x"], "z": protectedCellRaw["z"]}
	// The unwanted WoodLog and the forbidden Steel each sit on their own
	// separate cell (a second/third non-stacking item placed directly onto
	// a cell that already holds one was observed live to fail outright),
	// so every "source" scan below covers both cells, not one shared cell.
	sourceCells := []map[string]any{unwantedCell, protectedCell}
	destCellA := map[string]any{"x": destination["x"], "z": destination["z"]}
	destCellB := map[string]any{"x": na.AsNumber(destination["x"]) + 1, "z": destination["z"]}
	report["fixture_pawn"] = pawnID
	report["fixture_unwanted"] = unwantedID
	report["fixture_protected"] = protectedID
	report["fixture_corpse"] = corpseID

	// acquire takes a fresh authority lease: the explicit player-control
	// takeover path, mirroring recoveryserviceaccept's own single-acquire
	// pattern (only one dispatch happens in this tool).
	acquire := func(label string) error {
		// SetMode(Auto) at the current generation (#52): no lease, the
		// granted body's context.nativeGeneration is what preconditions carry.
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

	// pawnToken reads the exact native pawn-control snapshot token through
	// rimgovernor/observations_list_pawns, the same call
	// buildingruntime.WasteBoundary.InspectWaste's own bridge.ReadPawns issues.
	pawnToken := func(label string) (string, error) {
		reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{
			"scope":   map[string]any{"expectedIdentity": identity},
			"filter":  map[string]any{"ids": []string{pawnID}},
			"details": map[string]any{},
			"page":    map[string]any{"limit": 1},
		})
		if err != nil {
			return "", err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return "", err
		}
		rows := na.AsSlice(observed["pawns"])
		if len(rows) != 1 {
			return "", fmt.Errorf("%s: expected exactly one observed pawn, got %#v", label, observed)
		}
		row, _ := na.AsMap(rows[0])
		pawn, _ := na.AsMap(row["pawn"])
		if na.AsString(pawn["id"]) != pawnID {
			return "", fmt.Errorf("%s: unexpected pawn row: %#v", label, row)
		}
		snapshot, _ := na.AsMap(pawn["snapshot"])
		token := na.AsString(snapshot["token"])
		if token == "" {
			return "", fmt.Errorf("%s: missing pawn snapshot token: %#v", label, row)
		}
		return token, nil
	}

	// cellThings reads exact cells' things through
	// rimgovernor/observations_get_cells with things requested -- the real
	// production discovery path bridge.ReadWasteTarget issues (fixed in this
	// session: NativeObservationTools.GetCells previously refused any
	// things-field request as unsupported). Returns every observed thing row
	// across the requested cells, flattened.
	cellThings := func(label string, cells []map[string]any) ([]map[string]any, error) {
		reply, err := h.Wire(ctx, label, "observations_get_cells", map[string]any{
			"scope":      map[string]any{"expectedIdentity": identity},
			"exactCells": map[string]any{"cells": cells},
			"fields":     map[string]any{"terrain": false, "roof": false, "visibility": false, "traversal": false, "things": true},
			"page":       map[string]any{"limit": len(cells)},
		})
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, err
		}
		applied, _ := na.AsMap(observed["appliedFields"])
		if things, ok := na.AsBool(applied["things"]); !ok || !things {
			return nil, fmt.Errorf("%s: expected the things field to be applied, got %#v", label, applied)
		}
		var all []map[string]any
		for _, raw := range na.AsSlice(observed["cells"]) {
			row, _ := na.AsMap(raw)
			for _, rawThing := range na.AsSlice(row["things"]) {
				thing, _ := na.AsMap(rawThing)
				all = append(all, thing)
			}
		}
		return all, nil
	}
	findThing := func(things []map[string]any, id string) (map[string]any, bool) {
		for _, row := range things {
			thing, _ := na.AsMap(row["thing"])
			if thing != nil && na.AsString(thing["id"]) == id {
				return row, true
			}
		}
		return nil, false
	}
	thingToken := func(row map[string]any) (string, error) {
		thing, _ := na.AsMap(row["thing"])
		snapshot, _ := na.AsMap(thing["snapshot"])
		token := na.AsString(snapshot["token"])
		if token == "" {
			return "", fmt.Errorf("missing cell-thing snapshot token: %#v", row)
		}
		return token, nil
	}

	buildOperation := func(pID, pToken, tID, tToken string, unwanted []string) map[string]any {
		return map[string]any{"manageWaste": map[string]any{
			"target":      map[string]any{"entityId": tID, "expectedSnapshotToken": tToken},
			"pawn":        map[string]any{"entityId": pID, "expectedSnapshotToken": pToken},
			"unwantedIds": unwanted,
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

	// Before: source cell carries the unwanted item and the protected item,
	// each with a fresh CAS token discovered through the fixed native read.
	beforeSource, err := cellThings("before-source", sourceCells)
	if err != nil {
		return err
	}
	unwantedRow, ok := findThing(beforeSource, unwantedID)
	if !ok {
		return fmt.Errorf("before-source: unwanted item not found at the fixture source cell: %#v", beforeSource)
	}
	unwantedToken, err := thingToken(unwantedRow)
	if err != nil {
		return err
	}
	protectedRow, ok := findThing(beforeSource, protectedID)
	if !ok {
		return fmt.Errorf("before-source: protected item not found at the fixture source cell: %#v", beforeSource)
	}
	protectedToken, err := thingToken(protectedRow)
	if err != nil {
		return err
	}
	report["waste_before_source_things"] = len(beforeSource)

	token, err := pawnToken("pawn-before")
	if err != nil {
		return err
	}

	// Refusal 1: a genuinely forbidden (protected) item must be refused even
	// with a fresh, correctly-discovered CAS token and its id listed as
	// unwanted -- Protection() outranks Kind().
	protectedGeneration, err := currentGeneration("generation-protected")
	if err != nil {
		return err
	}
	protectedRequest := buildRequest("waste-protected", protectedGeneration,
		buildOperation(pawnID, token, protectedID, protectedToken, []string{protectedID}))
	if code, err := failureCode("protected-item", protectedRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("protected-item: expected an invalid-request refusal for a forbidden item, got %q", code)
	}

	// Refusal 2: a stale performer CAS token must be refused.
	staleGeneration, err := currentGeneration("generation-stale-pawn")
	if err != nil {
		return err
	}
	stalePawnRequest := buildRequest("waste-stale-pawn", staleGeneration,
		buildOperation(pawnID, "stale-pawn-token-00000000000000000000000000000000", unwantedID, unwantedToken, []string{unwantedID}))
	if code, err := failureCode("stale-pawn-token", stalePawnRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_OWNER_CONFLICT" && code != "FAILURE_CODE_NOT_FOUND" && code != "FAILURE_CODE_STALE_IDENTITY" {
		return fmt.Errorf("stale-pawn-token: expected a stale-identity/owner-conflict/not-found refusal, got %q", code)
	}

	// Refusal 3: a stale target CAS token must be refused (the item's own
	// self-computed CAS, distinct from NativePawnControlState's pawn token).
	staleTargetRequest := buildRequest("waste-stale-target", staleGeneration,
		buildOperation(pawnID, token, unwantedID, "stale-target-token-0000000000000000000000000000000", []string{unwantedID}))
	if code, err := failureCode("stale-target-token", staleTargetRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" && code != "FAILURE_CODE_NOT_FOUND" {
		return fmt.Errorf("stale-target-token: expected an invalid-request/not-found refusal, got %q", code)
	}

	// Preview: accepted, but never mutates the live item/job state.
	haulOperation := buildOperation(pawnID, token, unwantedID, unwantedToken, []string{unwantedID})
	previewReply, err := h.Wire(ctx, "preview-haul", "operations_preview", map[string]any{"identity": identity, "operation": haulOperation})
	if err != nil {
		return err
	}
	previewEvaluated, ok := na.AsMap(previewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview-haul: expected an evaluated reply, got %#v", previewReply)
	}
	if accepted, _ := na.AsBool(previewEvaluated["accepted"]); !accepted {
		return fmt.Errorf("preview-haul: expected the haul to be accepted, got %#v", previewEvaluated)
	}
	previewProjected, _ := na.AsMap(previewEvaluated["projected"])
	previewJob, _ := na.AsMap(previewProjected["job"])
	if na.AsString(previewJob["jobDef"]) != "HaulToCell" {
		return fmt.Errorf("preview-haul: expected a projected HaulToCell job, got %#v", previewJob)
	}
	if issued, _ := na.AsBool(previewJob["issued"]); issued {
		return fmt.Errorf("preview-haul: expected a dry-run preview to not issue a job: %#v", previewJob)
	}
	afterPreview, err := cellThings("after-preview-source", sourceCells)
	if err != nil {
		return err
	}
	if _, ok := findThing(afterPreview, unwantedID); !ok {
		return fmt.Errorf("after-preview-source: expected the unwanted item to remain at the source cell after a dry-run preview")
	}

	// Execute: the real native ManageWaste/HaulToCell dispatch. Re-read the
	// current nativeGeneration immediately before dispatch: it advances over
	// wall-clock/tick time independent of this tool's own writes.
	executeGeneration, err := currentGeneration("generation-before-execute")
	if err != nil {
		return err
	}
	executeRequest := buildRequest("waste-execute", executeGeneration, haulOperation)
	executeReply, err := h.Wire(ctx, "execute-haul", "operations_execute", executeRequest)
	if err != nil {
		return err
	}
	_, executeReceipt, err := na.Outcome(executeReply, "receipt")
	if err != nil {
		return err
	}
	executeApplied, ok := na.AsMap(executeReceipt["applied"])
	if !ok {
		return fmt.Errorf("execute-haul: expected an applied outcome, got %#v", executeReceipt)
	}
	executeObserved, _ := na.AsMap(executeApplied["observed"])
	executeJob, _ := na.AsMap(executeObserved["job"])
	if na.AsString(executeJob["pawnId"]) != pawnID || na.AsString(executeJob["jobDef"]) != "HaulToCell" {
		return fmt.Errorf("execute-haul: unexpected applied haul evidence: %#v", executeJob)
	}
	if issued, _ := na.AsBool(executeJob["issued"]); !issued {
		return fmt.Errorf("execute-haul: expected the native haul job to be issued, got %#v", executeJob)
	}
	if targetA, _ := na.AsMap(executeJob["targetA"]); na.AsString(targetA["thingId"]) != unwantedID {
		return fmt.Errorf("execute-haul: unexpected haul target: %#v", executeJob)
	}

	executePrecondition, _ := na.AsMap(executeRequest["precondition"])
	executeAttempt := map[string]any{"identity": identity, "attempt": executePrecondition["attempt"]}

	// Observe: run real game time forward until the fixture's unwanted item
	// is actually hauled by the real native job, not merely inferred from the
	// issued-job receipt.
	if _, err := na.ObserveCompleted(ctx, h, "observe-haul", 3*na.TicksPerDay, executeAttempt); err != nil {
		return err
	}

	// Independent confirmation, distinct from the receipt: the source cell no
	// longer carries the item, and it is now genuinely present in the
	// player-designated dirty stockpile.
	afterSource, err := cellThings("after-complete-source", sourceCells)
	if err != nil {
		return err
	}
	if _, ok := findThing(afterSource, unwantedID); ok {
		return fmt.Errorf("after-complete-source: expected the unwanted item to have left the source cell")
	}
	afterDestination, err := cellThings("after-complete-destination", []map[string]any{destCellA, destCellB})
	if err != nil {
		return err
	}
	if _, ok := findThing(afterDestination, unwantedID); !ok {
		return fmt.Errorf("after-complete-destination: expected the unwanted item to have arrived in the dirty stockpile: %#v", afterDestination)
	}
	report["waste_relocated"] = true

	// Replay: the exact same attempt returns an identical receipt.
	replayReply, err := h.Wire(ctx, "replay-haul", "operations_execute", executeRequest)
	if err != nil {
		return err
	}
	_, replay, err := na.Outcome(replayReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, executeReceipt) {
		return fmt.Errorf("replay-haul: replay of the same attempt returned a different receipt")
	}
	lookupReply, err := h.Wire(ctx, "lookup-haul", "receipts_lookup", executeAttempt)
	if err != nil {
		return err
	}
	_, lookup, err := na.Outcome(lookupReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(lookup, executeReceipt) {
		return fmt.Errorf("lookup-haul: expected the same receipt as execute, got %#v", lookup)
	}

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}
