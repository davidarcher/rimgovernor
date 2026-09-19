package wall

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{Name: "wall/adoption", Scope: "Smoke: explicit demolition adoption has a receipt; release removes adopted work and preserves unadopted player work.",
		Start: cases.Fixture{Op: "test/deconstruct_prepare", On: cases.Save{Name: baselineSave}}, Budget: time.Minute, Run: runAdoption})
}

func runAdoption(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	ids := na.AsSlice(s.Prepared()["ids"])
	if len(ids) != 6 {
		return fmt.Errorf("missing fixture targets")
	}
	// Target 1 already has a foreign order; target 0 is a colony building.
	if _, err := h.Call(ctx, "player-colony-order", "test/deconstruct_target", map[string]any{"target": ids[0], "action": "replace"}); err != nil {
		return err
	}
	grant, err := na.GrantAuto(ctx, h.WireFunc(), "adoption-authority", s.Identity())
	if err != nil {
		return err
	}
	execute := func(label string, op map[string]any) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "operations_execute", map[string]any{
			"precondition": map[string]any{"identity": s.Identity(), "expectedGeneration": fmt.Sprint(na.GrantGeneration(grant)),
				"attempt": map[string]any{"controllerSessionId": "adoption-smoke", "actionId": label, "attemptId": "1"}}, "operation": op})
		if err != nil {
			return nil, err
		}
		_, receipt, err := na.Outcome(reply, "receipt")
		return receipt, err
	}
	receipt, err := execute("adopt", map[string]any{"deconstruct": map[string]any{"target": map[string]any{"entityId": ids[0]}}})
	if err != nil {
		return err
	}
	applied, _ := na.AsMap(receipt["applied"])
	observed, _ := na.AsMap(applied["observed"])
	effect, _ := na.AsMap(observed["deconstruct"])
	if effect["targetId"] != ids[0] || na.AsString(effect["designationId"]) == "" {
		return fmt.Errorf("adoption lacks exact designation receipt: %#v", receipt)
	}
	s.Report()["adoption_receipt"] = receipt
	if _, err := execute("release", map[string]any{"releaseDeconstructions": map[string]any{}}); err != nil {
		return err
	}
	for _, i := range []int{0, 1} {
		row, err := h.Call(ctx, fmt.Sprintf("inspect-%d", i), "test/deconstruct_target", map[string]any{"target": ids[i], "action": "inspect"})
		if err != nil {
			return err
		}
		designated, _ := na.AsBool(row["designated"])
		present, _ := na.AsBool(row["present"])
		if !present || designated != (i == 1) {
			return fmt.Errorf("release did not distinguish adopted and foreign orders: %#v", row)
		}
	}
	return nil
}
