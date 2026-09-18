// The caravan/departure case exercises the CaravanDeparture vertical
// (G01.07f) end to end against a live game: the native FormCaravan operation
// (execute+preview+observe, NativeCaravanOperations.cs) and its CaravanCatalog
// census (NativeCaravanObservationTools.cs/NativeCaravanCatalog.cs), driven
// through the same rimgovernor/operations_execute wire contract Go's
// buildingruntime.CaravanDepartureBoundary depends on. Uses a private
// disposable fixture (test/caravan_departure_prepare) to guarantee a colony
// shaped for the admission policy (defaultCaravanDeparturePolicy in
// cmd/rimgovernor/serve_building.go: leave >=1 home colonist, >=5 days home
// food, a home doctor) that native random colony generation cannot
// deterministically produce, and to guarantee a real WoodLog cargo group and
// a real reachable destination settlement.
package caravan

import (
	"context"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const departureOwner = "native-caravan-departure-acceptance"

func init() {
	cases.Register(cases.Case{
		Name: "caravan/departure",
		Scope: "Native CaravanDeparture vertical: an actual FormCaravan dispatch through the " +
			"real Dialog_FormCaravan mechanism, CaravanCatalog cargo/route resolution, home-staffing admission " +
			"refusal, replay idempotency and a post-departure stale-catalog refusal.",
		Start:  cases.Fixture{Op: "test/caravan_departure_prepare", Args: map[string]any{"crewCount": 1}},
		Quiet:  na.QuietRequired,
		Budget: 5 * time.Minute,
		Run:    runDeparture,
	})
}

func runDeparture(ctx context.Context, s cases.Session) error {
	h, identity, names, prepared := s.Harness(), s.Identity(), s.Names(), s.Prepared()
	if !na.Contains(names, "rimgovernor/observations_read_caravan_catalog") {
		return fmt.Errorf("missing rimgovernor/observations_read_caravan_catalog in discovery")
	}

	var crewPawnIDs, remainingPawnIDs []string
	for _, raw := range na.AsSlice(prepared["crewPawnIds"]) {
		crewPawnIDs = append(crewPawnIDs, fmt.Sprint(raw))
	}
	for _, raw := range na.AsSlice(prepared["remainingPawnIds"]) {
		remainingPawnIDs = append(remainingPawnIDs, fmt.Sprint(raw))
	}
	destinationTile := na.AsNumber(prepared["destinationTile"])
	if len(crewPawnIDs) != 1 || len(remainingPawnIDs) < 2 || destinationTile <= 0 {
		return fmt.Errorf("caravan_departure_prepare: unexpected fixture identifiers: %#v", prepared)
	}

	grant, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity)
	if err != nil {
		return err
	}
	grantContext, _ := na.AsMap(grant["context"])

	// catalog reads the native FormCaravan catalog for the fixture's exact
	// destination, mirroring bridge.ReadCaravanCatalog's own extraction from
	// the same NativeCaravanCatalog.BuildDialog census, so the token/cargo
	// group/route facts used below are exactly what the Go boundary itself
	// would compute from this call.
	catalog := func(label string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "observations_read_caravan_catalog", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "destination": destinationTile,
			"page": map[string]any{"limit": 256},
		})
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, err
		}
		observedSnapshot, _ := na.AsMap(observed["snapshot"])
		observedContext, _ := na.AsMap(observedSnapshot["context"])
		if !na.DeepEqual(observedContext["identity"], identity) {
			return nil, fmt.Errorf("%s: catalog identity mismatch: %#v", label, observed)
		}
		completeness, _ := na.AsMap(observed["completeness"])
		page, _ := na.AsMap(completeness["page"])
		if complete, _ := na.AsBool(page["complete"]); !complete {
			return nil, fmt.Errorf("%s: expected a complete catalog page: %#v", label, completeness)
		}
		return observed, nil
	}

	observedCatalog, err := catalog("catalog-before")
	if err != nil {
		return err
	}
	snapshot, _ := na.AsMap(observedCatalog["snapshot"])
	catalogToken := na.AsString(snapshot["token"])
	if catalogToken == "" {
		return fmt.Errorf("catalog-before: missing catalog snapshot token: %#v", observedCatalog)
	}
	var woodGroupID, mealGroupID string
	var woodAvailable, mealAvailable float64
	for _, raw := range na.AsSlice(observedCatalog["cargoGroups"]) {
		group, _ := na.AsMap(raw)
		switch na.AsString(group["defName"]) {
		case "WoodLog":
			woodGroupID = na.AsString(group["groupId"])
			woodAvailable = na.AsNumber(group["count"])
		case "MealSimple":
			mealGroupID = na.AsString(group["groupId"])
			mealAvailable = na.AsNumber(group["count"])
		}
	}
	if woodGroupID == "" || woodAvailable < 10 {
		return fmt.Errorf("catalog-before: expected a WoodLog cargo group with at least 10 available, got %#v", observedCatalog)
	}
	if mealGroupID == "" || mealAvailable < 5 {
		return fmt.Errorf("catalog-before: expected a MealSimple cargo group with at least 5 available, got %#v", observedCatalog)
	}
	foundRoute := false
	for _, raw := range na.AsSlice(observedCatalog["routes"]) {
		route, _ := na.AsMap(raw)
		if na.AsNumber(route["destination"]) == destinationTile {
			foundRoute = true
			if reachable, _ := na.AsBool(route["reachable"]); !reachable {
				return fmt.Errorf("catalog-before: expected the fixture destination to be reachable, got %#v", route)
			}
		}
	}
	if !foundRoute {
		return fmt.Errorf("catalog-before: missing the fixture's requested destination route: %#v", observedCatalog)
	}

	cargo := []map[string]any{{"groupId": woodGroupID, "count": 10}, {"groupId": mealGroupID, "count": 5}}
	buildOperation := func(pawnIDs []string, cargoSelection []map[string]any) map[string]any {
		return map[string]any{"formCaravan": map[string]any{
			"expectedCatalogToken": catalogToken, "pawnIds": pawnIDs, "cargo": cargoSelection, "destinationTile": destinationTile,
		}}
	}

	// Preview: accepted, but never mutates the live map/dialog.
	previewReply, err := h.Wire(ctx, "preview", "operations_preview", map[string]any{
		"identity": identity, "operation": buildOperation(crewPawnIDs, cargo),
	})
	if err != nil {
		return err
	}
	evaluated, ok := na.AsMap(previewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview: expected an evaluated reply, got %#v", previewReply)
	}
	if accepted, _ := na.AsBool(evaluated["accepted"]); !accepted {
		return fmt.Errorf("preview: expected the departure to be accepted, got %#v", evaluated)
	}
	projected, _ := na.AsMap(evaluated["projected"])
	projectedCaravan, _ := na.AsMap(projected["caravan"])
	if na.AsNumber(projectedCaravan["destinationTile"]) != destinationTile {
		return fmt.Errorf("preview: unexpected projected destination: %#v", projectedCaravan)
	}
	if assemblyStarted, _ := na.AsBool(projectedCaravan["assemblyStarted"]); !assemblyStarted {
		return fmt.Errorf("preview: expected the dry-run projection to report assemblyStarted=true")
	}
	var projectedPawnIDs []string
	for _, raw := range na.AsSlice(projectedCaravan["pawnIds"]) {
		projectedPawnIDs = append(projectedPawnIDs, fmt.Sprint(raw))
	}
	if !na.DeepEqual(projectedPawnIDs, crewPawnIDs) {
		return fmt.Errorf("preview: unexpected projected crew: %v", projectedPawnIDs)
	}

	// Refusal: leaving zero colonists home is refused by native's own
	// FormCaravan admission check ("At least one colonist must remain
	// home."), the same home-staffing floor policy.CaravanDeparturePolicy's
	// MinimumHomeColonists enforces on the Go side.
	allPawnIDs := append(append([]string{}, crewPawnIDs...), remainingPawnIDs...)
	leaveNoOneHomeReply, err := h.Wire(ctx, "leave-no-one-home", "operations_preview", map[string]any{
		"identity": identity, "operation": buildOperation(allPawnIDs, nil),
	})
	if err != nil {
		return err
	}
	_, leaveNoOneHomeFailure, err := na.Outcome(leaveNoOneHomeReply, "failure")
	if err != nil {
		return err
	}
	if na.AsString(leaveNoOneHomeFailure["code"]) != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("leave-no-one-home: expected FAILURE_CODE_INVALID_REQUEST, got %#v", leaveNoOneHomeFailure)
	}

	// Both previews above were pure dry runs: the catalog token is unchanged.
	catalogAfterPreview, err := catalog("catalog-after-preview")
	if err != nil {
		return err
	}
	snapshotAfterPreview, _ := na.AsMap(catalogAfterPreview["snapshot"])
	if na.AsString(snapshotAfterPreview["token"]) != catalogToken {
		return fmt.Errorf("catalog-after-preview: expected an unchanged catalog token across dry-run previews")
	}

	// Execute: the real native Dialog_FormCaravan mechanism
	// (TryFormAndSendCaravan).
	buildRequest := func(actionID, attemptID string, operation map[string]any) map[string]any {
		return map[string]any{
			"precondition": map[string]any{
				"identity": identity, "expectedGeneration": grantContext["nativeGeneration"],
				"attempt": map[string]any{"controllerSessionId": departureOwner, "actionId": actionID, "attemptId": attemptID},
			},
			"operation": operation,
		}
	}
	executeRequest := buildRequest("caravan-departure", "1", buildOperation(crewPawnIDs, cargo))
	receiptReply, err := h.Wire(ctx, "execute", "operations_execute", executeRequest)
	if err != nil {
		return err
	}
	_, receipt, err := na.Outcome(receiptReply, "receipt")
	if err != nil {
		return err
	}
	// A native FormCaravan dispatch is admitted the moment the ledger accepts
	// the attempt; RimWorld's own post-formation steps (letters, tales,
	// messages) run after that and are not exception-safe, so native may
	// return "uncertain" (NativeCaravanOperations.Execute's outer catch)
	// rather than "applied" even though the caravan genuinely formed. Either
	// outcome is acceptable here as long as it is resolvable: an "applied"
	// receipt carries the confirmed evidence directly, an "uncertain" one is
	// resolved by the "observe" step below (receipts_observe_progress), which
	// requires native to have registered a caravan record for this attempt
	// regardless of which branch produced the outcome.
	var caravanID string
	var appliedPawnIDs []string
	applied, ok := na.AsMap(receipt["applied"])
	uncertain, uncertainOK := na.AsMap(receipt["uncertain"])
	if !ok && !uncertainOK {
		return fmt.Errorf("execute: expected an applied or uncertain outcome, got %#v", receipt)
	}
	if ok {
		appliedObserved, _ := na.AsMap(applied["observed"])
		appliedCaravan, _ := na.AsMap(appliedObserved["caravan"])
		if assemblyStarted, _ := na.AsBool(appliedCaravan["assemblyStarted"]); !assemblyStarted {
			return fmt.Errorf("execute: expected the caravan formation to be accepted, got %#v", appliedCaravan)
		}
		if na.AsNumber(appliedCaravan["destinationTile"]) != destinationTile {
			return fmt.Errorf("execute: unexpected destination: %#v", appliedCaravan)
		}
		caravanID = na.AsString(appliedCaravan["caravanId"])
		if caravanID == "" {
			return fmt.Errorf("execute: expected a caravan id, got %#v", appliedCaravan)
		}
		for _, raw := range na.AsSlice(appliedCaravan["pawnIds"]) {
			appliedPawnIDs = append(appliedPawnIDs, fmt.Sprint(raw))
		}
		if !na.DeepEqual(appliedPawnIDs, crewPawnIDs) {
			return fmt.Errorf("execute: unexpected applied crew: %v", appliedPawnIDs)
		}
	} else {
		detail := na.AsString(uncertain["detail"])
		fmt.Fprintf(os.Stderr, "execute: native returned uncertain (%s); resolving via observe\n", detail)
	}

	// Replay: the exact same attempt returns an identical receipt, and does
	// not attempt native formation a second time (a second real
	// TryFormAndSendCaravan call would either fail or form a second
	// caravan).
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

	precondition, _ := na.AsMap(executeRequest["precondition"])
	attempt := map[string]any{"identity": identity, "attempt": precondition["attempt"]}

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

	// Observe: progress reports a complete, correlated departure completion.
	// TryFormAndSendCaravan's underlying CaravanFormingUtility.StartFormingCaravan
	// does not create the Caravan world object synchronously: it starts a LordJob
	// that walks the crew to an exit tile over subsequent game ticks. An
	// "uncertain" execute outcome above means formation is genuinely still under
	// way (not merely an unconfirmed admission), so observing while still paused
	// would never resolve -- the clock must actually advance first.
	observeOnce := func(label string) (map[string]any, error) {
		progressReply, err := h.Wire(ctx, label, "receipts_observe_progress", attempt)
		if err != nil {
			return nil, err
		}
		_, progress, err := na.Outcome(progressReply, "progress")
		return progress, err
	}
	var progress map[string]any
	if ok {
		progress, err = observeOnce("observe")
		if err != nil {
			return err
		}
	} else {
		if _, err := h.Call(ctx, "unpause-for-formation", "rimworld/set_time_speed", map[string]any{"speed": "Superfast", "ultraSpeedBoost": false}); err != nil {
			return err
		}
		deadline := time.Now().Add(180 * time.Second)
		for {
			progress, err = observeOnce("observe")
			if err != nil {
				return err
			}
			if complete, _ := na.AsBool(progress["completeInspection"]); complete {
				break
			}
			if time.Now().After(deadline) {
				return fmt.Errorf("observe: caravan formation did not complete within 60s, got %#v", progress)
			}
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-time.After(500 * time.Millisecond):
			}
		}
		if _, err := h.Call(ctx, "pause-after-formation", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
			return err
		}
	}
	if complete, _ := na.AsBool(progress["completeInspection"]); !complete {
		return fmt.Errorf("observe: expected completeInspection=true, got %#v", progress)
	}
	completed, ok := na.AsMap(progress["completed"])
	if !ok {
		return fmt.Errorf("observe: expected a completed outcome, got %#v", progress)
	}
	completedEvidence, _ := na.AsMap(completed["evidence"])
	completedCaravan, _ := na.AsMap(completedEvidence["caravan"])
	if assemblyStarted, _ := na.AsBool(completedCaravan["assemblyStarted"]); !assemblyStarted {
		return fmt.Errorf("observe: expected assemblyStarted=true, got %#v", completedCaravan)
	}
	if na.AsNumber(completedCaravan["destinationTile"]) != destinationTile {
		return fmt.Errorf("observe: unexpected completed destination: %#v", completedCaravan)
	}
	var observedPawnIDs []string
	for _, raw := range na.AsSlice(completedCaravan["pawnIds"]) {
		observedPawnIDs = append(observedPawnIDs, fmt.Sprint(raw))
	}
	if !na.DeepEqual(observedPawnIDs, crewPawnIDs) {
		return fmt.Errorf("observe: unexpected completed crew: %v", observedPawnIDs)
	}
	if caravanID == "" {
		// Uncertain execute outcome: the caravan id is only known once
		// resolved here.
		caravanID = na.AsString(completedCaravan["caravanId"])
		if caravanID == "" {
			return fmt.Errorf("observe: expected a resolved caravan id, got %#v", completedCaravan)
		}
	} else if na.AsString(completedCaravan["caravanId"]) != caravanID {
		return fmt.Errorf("observe: unexpected completed caravan id: %#v", completedCaravan)
	}

	// Refusal: a brand-new attempt (fresh action id) after the crew has
	// already departed is refused as stale rather than silently re-admitted
	// or double-formed. The departed crew is no longer a home colonist, and
	// the catalog census (and therefore its CAS token) has changed, so
	// native refuses at the same "catalog changed; observe before new
	// admission" check the pre-execute refusal used, mirroring
	// questfulfillaccept's post-fulfillment-retry stale-identity refusal.
	staleRetryRequest := buildRequest("caravan-departure-again", "1", buildOperation(crewPawnIDs, cargo))
	staleReply, err := h.Wire(ctx, "post-departure-retry", "operations_execute", staleRetryRequest)
	if err != nil {
		return err
	}
	_, staleFailure, err := na.Outcome(staleReply, "failure")
	if err != nil {
		return err
	}
	if na.AsString(staleFailure["code"]) != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("post-departure-retry: expected FAILURE_CODE_INVALID_REQUEST, got %#v", staleFailure)
	}

	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}
