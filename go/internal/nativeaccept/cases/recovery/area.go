// The recovery/area case exercises the disaster-recovery allowed-area
// write (G01.07e, issue #27): a WorkSettingsIntent on Actions/Apply (#941,
// WorkSettingsActionHandler in NativeWorkSettings.cs) carrying only an
// allowed area. A colonist restricted to an outdoor Area during a
// registered ToxicFallout hazard is reassigned to a named roofed refuge
// Area, observed via rimgovernor/observations_list_pawns readback; the
// write is synchronous, so the case polls no game ticks.
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
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "recovery/area",
		Scope: "WorkSettingsIntent allowed area (disaster-recovery area) on Actions/Apply: a colonist " +
			"restricted to an outdoor area during a registered hazard is reassigned to a named roofed refuge, " +
			"observed via native readback; clearing under the hazard and a missing pawn are refused, a resent " +
			"key returns its first result and a fresh key applies again.",
		Start:  cases.Fixture{On: cases.LabStart(), Op: "test/recovery_area_prepare"},
		Budget: 5 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: runArea,
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
	if !na.Contains(s.Names(), "rimgovernor/operations_apply") {
		return fmt.Errorf("missing rimgovernor/operations_apply in discovery")
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

	// The fixture's registered hazard is a timed ToxicFallout, so the
	// colony-facts environment census (#235) must carry it with its native
	// remaining duration: the row Go's disaster review plans against.
	facts, err := h.Wire(ctx, "environment-census", "observations_read_colony_facts", map[string]any{"scope": map[string]any{"expectedIdentity": identity}})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(facts, "observed")
	if err != nil {
		return err
	}
	hazard := map[string]any(nil)
	for _, raw := range na.AsSlice(observed["environment"]) {
		row, _ := na.AsMap(raw)
		if na.AsString(row["defName"]) == "ToxicFallout" {
			hazard = row
		}
	}
	if hazard == nil {
		return fmt.Errorf("environment census lacks the fixture's ToxicFallout: %#v", observed["environment"])
	}
	permanent, _ := na.AsBool(hazard["permanent"])
	if permanent || na.AsNumber(hazard["ticksLeft"]) <= 0 || na.AsString(hazard["id"]) == "" {
		return fmt.Errorf("environment census row lacks a timed remaining duration: %#v", hazard)
	}
	report["environment_hazard"] = hazard

	// Actions/Apply needs current native authority; the allowed-area write
	// is synchronous, so one acquire covers the whole sequence.
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
		return err
	}

	// pawnArea reads the pawn's current allowedAreaId through
	// rimgovernor/observations_list_pawns. allowedAreaId is projected only
	// under the work detail flag (#167).
	pawnArea := func(label string) (string, error) {
		reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{
			"scope":   map[string]any{"expectedIdentity": identity},
			"filter":  map[string]any{"ids": []string{pawnID}},
			"details": map[string]any{"settings": false, "work": true},
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
		settings, _ := na.AsMap(row["settings"])
		return na.AsString(settings["allowedAreaId"]), nil
	}
	// apply sends one WorkSettingsIntent (bridge.workSettingsAction) and
	// returns its single result.
	apply := func(label, key, pID string, area map[string]any) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "operations_apply", map[string]any{"identity": identity, "actions": []any{map[string]any{
			"key": key, "workSettings": map[string]any{"pawnId": pID, "allowedArea": area},
		}}})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("%s: expected one result, got %#v", label, reply)
		}
		result, _ := na.AsMap(results[0])
		return result, nil
	}
	refusedWith := func(label string, result map[string]any, code string) error {
		refusal, ok := na.AsMap(result["refused"])
		if !ok || na.AsString(refusal["code"]) != code {
			return fmt.Errorf("%s: expected a %s refusal, got %#v", label, code, result)
		}
		report[label] = na.AsString(refusal["reason"])
		return nil
	}
	applied := func(label string, result map[string]any) error {
		receipt, _ := na.AsMap(result["applied"])
		outcome, _ := na.AsMap(receipt["applied"])
		observed, _ := na.AsMap(outcome["observed"])
		settings, _ := na.AsMap(observed["settings"])
		fields := na.AsSlice(settings["fields"])
		if len(fields) != 1 {
			return fmt.Errorf("%s: expected one applied allowed-area field, got %#v", label, result)
		}
		field, _ := na.AsMap(fields[0])
		if na.AsString(field["field"]) != "SETTINGS_FIELD_ALLOWED_AREA" || na.AsString(field["outcome"]) != "FIELD_OUTCOME_APPLIED" {
			return fmt.Errorf("%s: unexpected allowed-area field evidence: %#v", label, field)
		}
		return nil
	}

	beforeArea, err := pawnArea("pawn-before")
	if err != nil {
		return err
	}
	if beforeArea != outdoorID {
		return fmt.Errorf("pawn-before: expected the fixture colonist restricted to the outdoor area %q, got %q", outdoorID, beforeArea)
	}
	report["pawn_before_area"] = beforeArea

	// Refusals: clearing the restriction under the roof hazard, and a pawn
	// that is not on the map.
	result, err := apply("hazard-clear", "area-hazard-clear", pawnID, map[string]any{"clear": map[string]any{}})
	if err != nil {
		return err
	}
	if err := refusedWith("hazard_clear_refused", result, "FAILURE_CODE_INVALID_REQUEST"); err != nil {
		return err
	}
	if area, err := pawnArea("hazard-clear-unchanged"); err != nil || area != beforeArea {
		return fmt.Errorf("hazard clear changed restriction: %s, %v", area, err)
	}
	result, err = apply("missing-pawn", "area-missing-pawn", "Human_missing_pawn", map[string]any{"entityId": refugeID})
	if err != nil {
		return err
	}
	if err := refusedWith("missing_pawn_refused", result, "FAILURE_CODE_NOT_FOUND"); err != nil {
		return err
	}

	// Apply: the colonist moves to the refuge, read back from the pawn.
	refuge := map[string]any{"entityId": refugeID}
	first, err := apply("apply-area", "area-apply", pawnID, refuge)
	if err != nil {
		return err
	}
	if err := applied("apply-area", first); err != nil {
		return err
	}
	afterArea, err := pawnArea("pawn-after-apply")
	if err != nil {
		return err
	}
	if afterArea != refugeID {
		return fmt.Errorf("pawn-after-apply: expected the native colonist to be reassigned to the refuge area %q, got %q", refugeID, afterArea)
	}
	report["pawn_after_area"] = afterArea
	report["area_reassigned"] = true

	// Resend returns the first result; a fresh key applies again because
	// the restriction already holds.
	resent, err := apply("resend-area", "area-apply", pawnID, refuge)
	if err != nil {
		return err
	}
	if !na.DeepEqual(resent, first) {
		return fmt.Errorf("resend-area: resend of the same key returned a different result: %#v", resent)
	}
	again, err := apply("reapply-area", "area-reapply", pawnID, refuge)
	if err != nil {
		return err
	}
	if err := applied("reapply-area", again); err != nil {
		return err
	}

	return nil
}
