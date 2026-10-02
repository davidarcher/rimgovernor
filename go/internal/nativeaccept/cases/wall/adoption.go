package wall

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{Name: "wall/adoption", Scope: "Smoke: a DECONSTRUCT Designate adopts a player designation with applied evidence; revoking authority removes adopted work and preserves unadopted player work.",
		Start: cases.Fixture{Op: "test/deconstruct_prepare", On: cases.LabStart()}, Budget: time.Minute, Run: runAdoption})
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
	result, err := applyDeconstruct(ctx, s, "adopt", ids[0])
	if err != nil {
		return err
	}
	receipt, _ := na.AsMap(result["applied"])
	applied, _ := na.AsMap(receipt["applied"])
	observed, _ := na.AsMap(applied["observed"])
	effect, _ := na.AsMap(observed["deconstruct"])
	if effect["targetId"] != ids[0] || na.AsString(effect["designationId"]) == "" {
		return fmt.Errorf("adoption lacks exact designation evidence: %#v", result)
	}
	s.Report()["adoption_result"] = result
	if _, err := na.RevokeManual(ctx, h.WireFunc(), "release", s.Identity(), grant); err != nil {
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

// deconstructIntent is the enclosure-guarded DECONSTRUCT Designate arm of one
// building, with extra DesignateIntent fields (clearedGround, replaceWithWall).
func deconstructIntent(key string, target any, extra map[string]any) map[string]any {
	intent := map[string]any{"designation": "THING_DESIGNATION_DECONSTRUCT", "target": map[string]any{"id": target}, "guard": "DESIGNATION_GUARD_ENCLOSURE"}
	for k, v := range extra {
		intent[k] = v
	}
	return map[string]any{"key": key, "designate": intent}
}

// applyDeconstruct sends one DECONSTRUCT Designate under key and returns its
// ActionResult; a refusal or failure is an error.
func applyDeconstruct(ctx context.Context, s cases.Session, key string, target any) (map[string]any, error) {
	reply, err := s.Harness().Wire(ctx, key, "operations_apply", map[string]any{"identity": s.Identity(),
		"actions": []any{deconstructIntent(key, target, nil)}})
	if err != nil {
		return nil, err
	}
	results := na.AsSlice(reply["results"])
	if len(results) != 1 {
		return nil, fmt.Errorf("%s: expected one result: %#v", key, reply)
	}
	result, _ := na.AsMap(results[0])
	if _, ok := na.AsMap(result["applied"]); !ok {
		return nil, fmt.Errorf("%s: not applied: %#v", key, result)
	}
	return result, nil
}
