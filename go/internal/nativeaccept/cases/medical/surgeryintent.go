package medical

import (
	"context"
	"fmt"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "medical/surgery-intent",
		Scope: "SurgeryIntent on Actions/Apply (#1162): a peg-leg bill on a colonist missing a leg is QUEUED past an " +
			"unrelated bill, a resent intent applies again with the same Bill_Medical, and organ removal on a " +
			"prisoner is refused without acknowledge_violation.",
		Start:  cases.Fixture{Op: "test/surgery_intent_prepare", On: cases.LabStart()},
		Budget: 2 * time.Minute,
		Run:    surgeryIntent,
	})
}

func surgeryIntent(ctx context.Context, s cases.Session) error {
	h, identity, prepared, report := s.Harness(), s.Identity(), s.Prepared(), s.Report()
	patient, prisoner := na.AsString(prepared["patientId"]), na.AsString(prepared["prisonerId"])
	if patient == "" || prisoner == "" {
		return fmt.Errorf("prepare: missing patient or prisoner: %#v", prepared)
	}
	// The fixture edits health outside authority, so authority is granted after it.
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
		return err
	}
	apply := func(key string, surgery map[string]any) (map[string]any, error) {
		reply, err := h.Wire(ctx, key, "operations_apply", map[string]any{"identity": identity, "actions": []any{map[string]any{"key": key, "surgery": surgery}}})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("%s: expected one result, got %#v", key, reply)
		}
		result, _ := na.AsMap(results[0])
		return result, nil
	}
	queued := func(key string) (string, error) {
		result, err := apply(key, map[string]any{"pawnId": patient, "recipeDef": "InstallPegLeg", "partIndex": prepared["part"]})
		if err != nil {
			return "", err
		}
		applied, _ := na.AsMap(result["applied"])
		applied, _ = na.AsMap(applied["applied"])
		observed, _ := na.AsMap(applied["observed"])
		effect, _ := na.AsMap(observed["surgeryBill"])
		bill := na.AsString(effect["billId"])
		if na.AsString(effect["state"]) != "SURGERY_STATE_QUEUED" || bill == "" || na.AsString(effect["pawnId"]) != patient {
			return "", fmt.Errorf("%s: expected a QUEUED surgery bill, got %#v", key, result)
		}
		return bill, nil
	}
	first, err := queued("surgery-queue")
	if err != nil {
		return err
	}
	again, err := queued("surgery-requeue")
	if err != nil {
		return err
	}
	if again != first {
		return fmt.Errorf("re-apply queued a second bill %s beside %s", again, first)
	}
	report["bill"] = first
	result, err := apply("surgery-violation", map[string]any{"pawnId": prisoner, "recipeDef": "RemoveBodyPart", "partIndex": prepared["kidney"]})
	if err != nil {
		return err
	}
	refused, ok := na.AsMap(result["refused"])
	if !ok || !strings.Contains(na.AsString(refused["reason"]), "violation") {
		return fmt.Errorf("unacknowledged organ removal: expected a violation refusal, got %#v", result)
	}
	report["violation_refusal"] = refused["reason"]
	return nil
}
