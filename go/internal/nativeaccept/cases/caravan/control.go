// The caravan/control case exercises the TravelCaravan vertical (G01.08)
// end to end against a live game: an already-formed real player caravan,
// holding it in place through its native path follower (Stop), routing it to
// an already-scouted neighboring tile (Move), sending it home
// (ReturnHome), exact CAS/stale-identity refusal, replay idempotency and
// durable lookup -- through the same rimgovernor/operations_execute wire
// contract Go's bridge.TravelCaravanWriter drives. Uses a private disposable
// fixture (test/caravan_control_prepare) since a deterministic reachable
// destination and caravan crew cannot be produced from native random world
// generation alone.
package caravan

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const controlOwner = "native-caravan-control-acceptance"

func init() {
	cases.Register(cases.Case{
		Name: "caravan/control",
		Scope: "Native TravelCaravan vertical: hold (Stop), route (Move) and return-home dispositions " +
			"of an already-formed real player caravan through its actual native path follower, exact CAS/" +
			"stale-identity refusal, replay idempotency and durable lookup.",
		Start:  cases.Fixture{Op: "test/caravan_control_prepare"},
		Quiet:  na.QuietRequired,
		Budget: 5 * time.Minute,
		Run:    runControl,
	})
}

func runControl(ctx context.Context, s cases.Session) error {
	h, identity, names, prepared := s.Harness(), s.Identity(), s.Names(), s.Prepared()
	if !na.Contains(names, "rimgovernor/observations_read_world_progression") {
		return fmt.Errorf("missing rimgovernor/observations_read_world_progression in discovery")
	}

	caravanID := na.AsString(prepared["caravanId"])
	homeTile := na.AsNumber(prepared["homeTile"])
	destinationTile := int32(na.AsNumber(prepared["destinationTile"]))
	if caravanID == "" {
		return fmt.Errorf("caravan_control_prepare: unexpected fixture identifiers: %#v", prepared)
	}

	grant, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity)
	if err != nil {
		return err
	}
	grantContext, _ := na.AsMap(grant["context"])

	// target reads the world-progression census for the fixture's exact
	// caravan, mirroring bridge.TravelCaravanToken's own extraction from the
	// same census (NativeWorldProgressionObservation.cs's Caravans()), so the
	// token used below is exactly what the Go boundary itself would compute
	// from this call.
	target := func(label string) (caravanRow map[string]any, err error) {
		reply, err := h.Wire(ctx, label, "observations_read_world_progression", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "includeStorage": false, "page": map[string]any{"limit": 64},
		})
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, err
		}
		completeness, _ := na.AsMap(observed["completeness"])
		page, _ := na.AsMap(completeness["page"])
		if complete, _ := na.AsBool(page["complete"]); !complete || na.AsNumber(completeness["unreadable"]) != 0 {
			return nil, fmt.Errorf("%s: incomplete or unreadable world progression page: %#v", label, completeness)
		}
		for _, raw := range na.AsSlice(observed["caravans"]) {
			row, _ := na.AsMap(raw)
			caravan, _ := na.AsMap(row["caravan"])
			if na.AsString(caravan["id"]) == caravanID {
				caravanRow = row
			}
		}
		if caravanRow == nil {
			return nil, fmt.Errorf("%s: fixture caravan not found in world progression: %#v", label, observed)
		}
		return caravanRow, nil
	}

	caravanRow, err := target("target-before")
	if err != nil {
		return err
	}
	if na.AsNumber(caravanRow["tile"]) != homeTile {
		return fmt.Errorf("target-before: expected the caravan still at the home tile, got %#v", caravanRow)
	}
	if moving, _ := na.AsBool(caravanRow["moving"]); moving {
		return fmt.Errorf("target-before: expected a stationary caravan, got %#v", caravanRow)
	}
	stationaryToken := caravanToken(caravanID, int64(homeTile), false)

	buildOperation := func(kind string, cToken string, tile *int32) map[string]any {
		travel := map[string]any{"caravan": map[string]any{"entityId": caravanID, "expectedSnapshotToken": cToken}, "kind": kind}
		if tile != nil {
			travel["destinationTile"] = *tile
		}
		return map[string]any{"travelCaravan": travel}
	}
	buildRequest := func(actionID, attemptID string, operation map[string]any) map[string]any {
		return map[string]any{
			"precondition": map[string]any{
				"identity": identity, "expectedGeneration": grantContext["nativeGeneration"],
				"attempt": map[string]any{"controllerSessionId": controlOwner, "actionId": actionID, "attemptId": attemptID},
			},
			"operation": operation,
		}
	}

	// Refusal 1: a stale token (recomputed with the wrong moving flag) must
	// be refused as stale identity rather than silently accepted.
	staleToken := caravanToken(caravanID, int64(homeTile), true)
	staleRequest := buildRequest("caravan-hold-stale", "1", buildOperation("TRAVEL_KIND_STOP", staleToken, nil))
	if code, err := failureCode(ctx, h, "stale-token-hold", staleRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_STALE_IDENTITY" {
		return fmt.Errorf("stale-token-hold: expected FAILURE_CODE_STALE_IDENTITY, got %q", code)
	}

	// Preview: Move is accepted but never mutates the live path follower.
	previewReply, err := h.Wire(ctx, "preview", "operations_preview", map[string]any{
		"identity": identity, "operation": buildOperation("TRAVEL_KIND_MOVE", stationaryToken, &destinationTile),
	})
	if err != nil {
		return err
	}
	evaluated, ok := na.AsMap(previewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview: expected an evaluated reply, got %#v", previewReply)
	}
	if accepted, _ := na.AsBool(evaluated["accepted"]); !accepted {
		return fmt.Errorf("preview: expected the route to be accepted, got %#v", evaluated)
	}
	afterPreviewRow, err := target("target-after-preview")
	if err != nil {
		return err
	}
	if moving, _ := na.AsBool(afterPreviewRow["moving"]); moving {
		return fmt.Errorf("target-after-preview: expected the dry-run preview to leave the caravan stationary: %#v", afterPreviewRow)
	}

	// Execute: Move actually starts the native path follower.
	moveRequest := buildRequest("caravan-route", "1", buildOperation("TRAVEL_KIND_MOVE", stationaryToken, &destinationTile))
	moveReceiptReply, err := h.Wire(ctx, "execute-move", "operations_execute", moveRequest)
	if err != nil {
		return err
	}
	_, moveReceipt, err := na.Outcome(moveReceiptReply, "receipt")
	if err != nil {
		return err
	}
	moveApplied, ok := na.AsMap(moveReceipt["applied"])
	if !ok {
		return fmt.Errorf("execute-move: expected an applied outcome, got %#v", moveReceipt)
	}
	moveObserved, _ := na.AsMap(moveApplied["observed"])
	moveCaravan, _ := na.AsMap(moveObserved["caravan"])
	if pathStarted, _ := na.AsBool(moveCaravan["pathStarted"]); !pathStarted {
		return fmt.Errorf("execute-move: expected pathStarted=true, got %#v", moveCaravan)
	}
	if na.AsNumber(moveCaravan["destinationTile"]) != float64(destinationTile) {
		return fmt.Errorf("execute-move: unexpected destination tile: %#v", moveCaravan)
	}

	movingRow, err := target("target-after-move")
	if err != nil {
		return err
	}
	if moving, _ := na.AsBool(movingRow["moving"]); !moving {
		return fmt.Errorf("target-after-move: expected the caravan to actually be moving, got %#v", movingRow)
	}
	movingTile := int64(na.AsNumber(movingRow["tile"]))
	movingToken := caravanToken(caravanID, movingTile, true)

	// Replay: the exact same attempt returns an identical receipt.
	replayReply, err := h.Wire(ctx, "replay-move", "operations_execute", moveRequest)
	if err != nil {
		return err
	}
	_, replay, err := na.Outcome(replayReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, moveReceipt) {
		return fmt.Errorf("replay-move: replay of the same attempt returned a different receipt")
	}

	// Durable lookup: receipts_lookup independently returns the same receipt.
	movePrecondition, _ := na.AsMap(moveRequest["precondition"])
	moveAttempt := map[string]any{"identity": identity, "attempt": movePrecondition["attempt"]}
	lookupReply, err := h.Wire(ctx, "lookup-move", "receipts_lookup", moveAttempt)
	if err != nil {
		return err
	}
	_, lookup, err := na.Outcome(lookupReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(lookup, moveReceipt) {
		return fmt.Errorf("lookup-move: expected the same receipt as execute, got %#v", lookup)
	}

	// Hold: stop the now-moving caravan through the native path follower.
	holdRequest := buildRequest("caravan-hold", "1", buildOperation("TRAVEL_KIND_STOP", movingToken, nil))
	holdReceiptReply, err := h.Wire(ctx, "execute-hold", "operations_execute", holdRequest)
	if err != nil {
		return err
	}
	_, holdReceipt, err := na.Outcome(holdReceiptReply, "receipt")
	if err != nil {
		return err
	}
	holdApplied, ok := na.AsMap(holdReceipt["applied"])
	if !ok {
		return fmt.Errorf("execute-hold: expected an applied outcome, got %#v", holdReceipt)
	}
	holdObserved, _ := na.AsMap(holdApplied["observed"])
	holdCaravan, _ := na.AsMap(holdObserved["caravan"])
	if stopped, _ := na.AsBool(holdCaravan["stopped"]); !stopped {
		return fmt.Errorf("execute-hold: expected stopped=true, got %#v", holdCaravan)
	}

	stoppedRow, err := target("target-after-hold")
	if err != nil {
		return err
	}
	if moving, _ := na.AsBool(stoppedRow["moving"]); moving {
		return fmt.Errorf("target-after-hold: expected the caravan to be stationary, got %#v", stoppedRow)
	}
	stoppedTile := int64(na.AsNumber(stoppedRow["tile"]))
	stoppedToken := caravanToken(caravanID, stoppedTile, false)

	// Return home: route the held caravan back to the home tile.
	returnRequest := buildRequest("caravan-return-home", "1", buildOperation("TRAVEL_KIND_RETURN_HOME", stoppedToken, nil))
	returnReceiptReply, err := h.Wire(ctx, "execute-return-home", "operations_execute", returnRequest)
	if err != nil {
		return err
	}
	_, returnReceipt, err := na.Outcome(returnReceiptReply, "receipt")
	if err != nil {
		return err
	}
	returnApplied, ok := na.AsMap(returnReceipt["applied"])
	if !ok {
		return fmt.Errorf("execute-return-home: expected an applied outcome, got %#v", returnReceipt)
	}
	returnObserved, _ := na.AsMap(returnApplied["observed"])
	returnCaravan, _ := na.AsMap(returnObserved["caravan"])
	if pathStarted, _ := na.AsBool(returnCaravan["pathStarted"]); !pathStarted {
		return fmt.Errorf("execute-return-home: expected pathStarted=true, got %#v", returnCaravan)
	}
	if na.AsNumber(returnCaravan["destinationTile"]) != homeTile {
		return fmt.Errorf("execute-return-home: expected destinationTile=%v, got %#v", homeTile, returnCaravan)
	}

	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}

// failureCode wires request through operations_execute and returns the
// failure code, mirroring questfulfillaccept's/the movement case's helper of the
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

// caravanToken reproduces NativeCaravanTravel.CaravanToken/Go's unexported
// travelCaravanToken exactly (same joined-string SHA256 hex, lowercase
// booleans), from a world-progression caravan row's id/tile/moving alone, so
// this case can self-compute the same CAS token the native
// contract and the Go boundary both use without needing to export that
// helper from internal/bridge.
func caravanToken(id string, tile int64, moving bool) string {
	movingText := "false"
	if moving {
		movingText = "true"
	}
	sum := sha256.Sum256([]byte(fmt.Sprintf("%s|%d|%s", id, tile, movingText)))
	return "caravan-travel-" + hex.EncodeToString(sum[:])
}
