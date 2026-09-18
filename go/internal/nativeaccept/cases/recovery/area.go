// The recovery/area case exercises the disaster-recovery allowed-area
// native dispatch vertical (G01.07e, issue #27): NativeWorkSettings' PatchPawn
// handler (integrations/rimgovernor-native/src/Bridge/Protocol/
// NativeWorkSettings.cs), extended to admit an AllowedArea assignment
// alongside -- or instead of -- work priorities, through the same
// CAS/admission/receipt token domain regular work-priority writes already
// use. A colonist genuinely restricted to an outdoor Area during a
// registered ToxicFallout hazard actually has their allowed-area
// restriction reassigned to a real named roofed refuge Area by a real
// native PatchPawn execute, observed via a real
// rimgovernor/observations_list_pawns readback (not just a receipt), not a
// job/tick-driven effect: the native write is synchronous, so unlike
// recoveryserviceaccept this tool does not poll game ticks to completion.
//
// Uses the disposable test/recovery_area_prepare fixture
// (RecoveryAreaFixture.cs) since a deterministic roofed refuge Area,
// outdoor Area and hazard-restricted colonist cannot be relied on from
// native random pawn generation and starting colony state, mirroring
// recoveryserviceaccept's/animalcontainmentaccept's own fixture-first
// pattern.
package recovery

import (
	"context"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const areaOwner = "native-recovery-area-acceptance"

func init() {
	cases.Register(cases.Case{
		Name: "recovery/area",
		Scope: "Native PatchPawn AllowedArea (disaster-recovery area) dispatch: a colonist genuinely " +
			"restricted to an outdoor area during a registered hazard actually has their allowed-area restriction " +
			"reassigned to a real named roofed refuge by a real native PatchPawn execute issued through the typed " +
			"operations contract, exact CAS/stale-identity refusal, preview non-mutation, real allowed-area change " +
			"observed via native readback (not just a receipt), and replay idempotency.",
		Start:  cases.Fixture{Op: "test/recovery_area_prepare"},
		Budget: 5 * time.Minute,
		Run:    runArea,
	})
}

func runArea(ctx context.Context, s cases.Session) error {
	// Fixture: one roofed refuge Area, one outdoor Area, and one existing
	// colonist restricted to the outdoor area under a registered hazard.
	// Run this BEFORE acquiring authority: the fixture builds/restricts
	// directly outside any authority.Owned() scope, and
	// NativeControlAuthority.RevokeExternal revokes any held lease for such
	// external activity regardless of who holds it, mirroring
	// recoveryserviceaccept's own ordering.
	report := s.Report()
	h := s.Harness()
	identity := s.Identity()
	prepared := s.Prepared()
	if !na.Contains(s.Names(), "rimgovernor/operations_execute") {
		return fmt.Errorf("missing rimgovernor/operations_execute in discovery")
	}
	pawnID := na.AsString(prepared["pawn"])
	refugeID := na.AsString(prepared["refuge"])
	outdoorID := na.AsString(prepared["outdoor"])
	if pawnID == "" || refugeID == "" || outdoorID == "" {
		return fmt.Errorf("prepare: missing fixture pawn/refuge/outdoor ids: %#v", prepared)
	}
	report["fixture_pawn"] = pawnID
	report["fixture_refuge"] = refugeID
	report["fixture_outdoor"] = outdoorID

	// acquire takes a fresh authority lease: the explicit player-control
	// takeover path. Only one dispatch happens in this tool (no Fast-speed
	// tick-advance window precedes it, since the PatchPawn AllowedArea
	// effect is synchronous), so a single acquire before the whole
	// stale-token/preview/execute sequence is enough.
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

	// pawnState reads the exact native pawn-control snapshot token and the
	// pawn's current allowedAreaId through rimgovernor/observations_list_pawns,
	// the same call buildingruntime/work.WorkBoundary.readWork's own
	// bridge.ReadRoutinePawns issues. allowedAreaId is projected only under
	// the work detail flag (#167): it belongs to the work snapshot token
	// domain, not the care-policy row the default details select.
	pawnState := func(label string) (token, areaID string, err error) {
		reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{
			"scope":   map[string]any{"expectedIdentity": identity},
			"filter":  map[string]any{"ids": []string{pawnID}},
			"details": map[string]any{"settings": false, "work": true},
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
		settings, _ := na.AsMap(row["settings"])
		snapshot, _ := na.AsMap(settings["snapshot"])
		token = na.AsString(snapshot["token"])
		if token == "" {
			return "", "", fmt.Errorf("%s: missing pawn snapshot token: %#v", label, row)
		}
		areaID = na.AsString(settings["allowedAreaId"])
		return token, areaID, nil
	}

	// buildOperation mirrors bridge.workOperation
	// (go/internal/bridge/work_assignment.go): a work-only PatchPawn with no
	// WorkPriority rows and an Assignment naming the target area.
	buildOperation := func(pID, pToken, areaID string) map[string]any {
		return map[string]any{"patchPawn": map[string]any{
			"pawn":        map[string]any{"entityId": pID, "expectedSnapshotToken": pToken},
			"allowedArea": map[string]any{"entityId": areaID},
		}}
	}
	buildRequest := func(actionID string, generation any, operation map[string]any) map[string]any {
		return map[string]any{
			"precondition": map[string]any{
				"identity": identity, "expectedGeneration": generation,
				"attempt": map[string]any{"controllerSessionId": areaOwner, "actionId": actionID, "attemptId": "1"},
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

	beforeToken, beforeArea, err := pawnState("pawn-before")
	if err != nil {
		return err
	}
	if beforeArea != outdoorID {
		return fmt.Errorf("pawn-before: expected the fixture colonist restricted to the outdoor area %q, got %q", outdoorID, beforeArea)
	}
	report["pawn_before_area"] = beforeArea

	// Refusal: a stale performer CAS token must be refused.
	staleGeneration, err := currentGeneration("generation-stale")
	if err != nil {
		return err
	}
	staleRequest := buildRequest("area-stale-token", staleGeneration,
		buildOperation(pawnID, "stale-pawn-token-00000000000000000000000000000000", refugeID))
	if code, err := failureCode("stale-token", staleRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_OWNER_CONFLICT" && code != "FAILURE_CODE_NOT_FOUND" && code != "FAILURE_CODE_STALE_IDENTITY" && code != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("stale-token: expected a stale-identity/owner-conflict/invalid-request refusal, got %q", code)
	}

	// Preview: accepted, but never mutates the live pawn settings.
	areaOperation := buildOperation(pawnID, beforeToken, refugeID)
	previewReply, err := h.Wire(ctx, "preview-area", "operations_preview", map[string]any{"identity": identity, "operation": areaOperation})
	if err != nil {
		return err
	}
	previewEvaluated, ok := na.AsMap(previewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview-area: expected an evaluated reply, got %#v", previewReply)
	}
	if accepted, _ := na.AsBool(previewEvaluated["accepted"]); !accepted {
		return fmt.Errorf("preview-area: expected the area assignment to be accepted, got %#v", previewEvaluated)
	}
	_, afterPreviewArea, err := pawnState("pawn-after-preview")
	if err != nil {
		return err
	}
	if afterPreviewArea != outdoorID {
		return fmt.Errorf("pawn-after-preview: expected an unchanged allowed area from a dry-run preview, got %q (was %q)", afterPreviewArea, outdoorID)
	}

	// Execute: the real native PatchPawn AllowedArea dispatch. Re-read the
	// current nativeGeneration immediately before dispatch: it advances over
	// wall-clock/tick time independent of this tool's own writes.
	executeGeneration, err := currentGeneration("generation-before-execute")
	if err != nil {
		return err
	}
	executeRequest := buildRequest("area-execute", executeGeneration, areaOperation)
	executeReply, err := h.Wire(ctx, "execute-area", "operations_execute", executeRequest)
	if err != nil {
		return err
	}
	_, executeReceipt, err := na.Outcome(executeReply, "receipt")
	if err != nil {
		return err
	}
	executeApplied, ok := na.AsMap(executeReceipt["applied"])
	if !ok {
		return fmt.Errorf("execute-area: expected an applied outcome, got %#v", executeReceipt)
	}
	executeObserved, _ := na.AsMap(executeApplied["observed"])
	executeSettings, _ := na.AsMap(executeObserved["settings"])
	executeFields := na.AsSlice(executeSettings["fields"])
	if len(executeFields) != 1 {
		return fmt.Errorf("execute-area: expected exactly one settings field in the effect evidence, got %#v", executeSettings)
	}
	executeField, _ := na.AsMap(executeFields[0])
	if na.AsString(executeField["field"]) != "SETTINGS_FIELD_ALLOWED_AREA" || na.AsString(executeField["outcome"]) != "FIELD_OUTCOME_APPLIED" {
		return fmt.Errorf("execute-area: unexpected allowed-area field evidence: %#v", executeField)
	}

	// Observe: the native write is synchronous (a settings assignment, not
	// a job), so the readback is issued immediately -- no Fast-speed
	// tick-advance window is needed, unlike a job-driven dispatch such as
	// recoveryserviceaccept's own repair job.
	_, afterArea, err := pawnState("pawn-after-execute")
	if err != nil {
		return err
	}
	if afterArea != refugeID {
		return fmt.Errorf("pawn-after-execute: expected the native colonist to be reassigned to the refuge area %q, got %q", refugeID, afterArea)
	}
	report["pawn_after_area"] = afterArea
	report["area_reassigned"] = true

	// Replay: the exact same attempt returns an identical receipt.
	replayReply, err := h.Wire(ctx, "replay-area", "operations_execute", executeRequest)
	if err != nil {
		return err
	}
	_, replay, err := na.Outcome(replayReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, executeReceipt) {
		return fmt.Errorf("replay-area: replay of the same attempt returned a different receipt")
	}
	executePrecondition, _ := na.AsMap(executeRequest["precondition"])
	executeAttempt := map[string]any{"identity": identity, "attempt": executePrecondition["attempt"]}
	lookupReply, err := h.Wire(ctx, "lookup-area", "receipts_lookup", executeAttempt)
	if err != nil {
		return err
	}
	_, lookup, err := na.Outcome(lookupReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(lookup, executeReceipt) {
		return fmt.Errorf("lookup-area: expected the same receipt as execute, got %#v", lookup)
	}

	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}
