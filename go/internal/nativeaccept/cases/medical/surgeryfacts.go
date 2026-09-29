package medical

import (
	"context"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "medical/surgery-facts",
		Scope: "list_pawns' health detail carries the surgery facts (#1161): a colonist missing a leg with a " +
			"prosthetic in stock reports the missing part and a restore operation on it with a vanilla " +
			"success_chance > 0. A native read contract: the recipe discovery and SurgeryOutcomeEffectDef " +
			"quality are vanilla code a Go snapshot cannot exercise.",
		Start: cases.Fixture{On: cases.LabStart(), Op: "test/medical_management_setup",
			Args: map[string]any{"disease": false}},
		Budget: cases.LabBudget,
		Run:    surgeryFacts,
	})
}

func surgeryFacts(ctx context.Context, s cases.Session) error {
	prepared := s.Prepared()
	patient := na.AsString(prepared["surgical"])
	part, ok := prepared["part"].(float64)
	if patient == "" || !ok {
		return fmt.Errorf("medical_management_setup reply lacks surgical/part: %#v", prepared)
	}
	h := s.Harness()
	identity, err := na.ReadIdentity(ctx, h, "surgery-facts-identity")
	if err != nil {
		return err
	}
	reply, err := h.Wire(ctx, "surgery-facts-readback", "observations_list_pawns", map[string]any{
		"scope":   map[string]any{"expectedIdentity": identity},
		"filter":  map[string]any{"colonist": true, "humanlike": true, "animal": false},
		"details": map[string]any{"health": true, "needs": false, "equipment": false, "biography": false, "settings": false, "social": false, "animals": false},
	})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	for _, raw := range na.AsSlice(observed["pawns"]) {
		row, _ := na.AsMap(raw)
		ref, _ := na.AsMap(row["pawn"])
		if na.AsString(ref["id"]) != patient {
			continue
		}
		health, _ := na.AsMap(row["health"])
		s.Report()["surgery_health"] = map[string]any{"missingParts": health["missingParts"], "operations": health["operations"]}
		return surgeryFactsObserve(health, int(part))
	}
	return fmt.Errorf("surgical patient %s not in the pawn read", patient)
}

// surgeryFactsObserve requires the missing leg and a restore operation on it
// with a positive vanilla success chance and at least one eligible doctor.
func surgeryFactsObserve(health map[string]any, part int) error {
	missing := false
	for _, raw := range na.AsSlice(health["missingParts"]) {
		m, _ := na.AsMap(raw)
		if index, ok := m["partIndex"].(float64); ok && int(index) == part {
			missing = true
		}
	}
	if !missing {
		return fmt.Errorf("missing leg %d not reported: %#v", part, health["missingParts"])
	}
	for _, raw := range na.AsSlice(health["operations"]) {
		op, _ := na.AsMap(raw)
		index, ok := op["partIndex"].(float64)
		chance, _ := op["successChance"].(float64)
		if ok && int(index) == part && na.AsString(op["kind"]) == "SURGERY_KIND_RESTORE" && chance > 0 {
			return nil
		}
	}
	return fmt.Errorf("no restore operation with success_chance > 0 on part %d: %#v", part, health["operations"])
}
