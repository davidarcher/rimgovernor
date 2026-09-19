package wall

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/takeover"
)

func init() {
	cases.Register(cases.Case{
		Name: "takeover/demolition-designation", Scope: "Manual player demolition designation on a colony wall is explicitly adopted in Auto; native receipt journal and pawn demolition readback prove completion.",
		Start: cases.Save{Name: baselineSave}, RequiredOps: []string{"test/deconstruct_prepare", "test/deconstruct_target"}, QuietWorld: true,
		Budget: 3 * time.Minute, Run: runDemolitionTakeover,
	})
}

func runDemolitionTakeover(ctx context.Context, s cases.Session) error {
	if err := takeover.Manual(ctx, s); err != nil {
		return err
	}
	h, identity := s.Harness(), s.Identity()
	fixture, err := h.Call(ctx, "manual-wall-fixture", "test/deconstruct_prepare", map[string]any{})
	if err != nil {
		return err
	}
	ids := na.AsSlice(fixture["ids"])
	if len(ids) != 6 {
		return fmt.Errorf("missing demolition targets: %v", fixture)
	}
	target := ids[5]
	for _, action := range []string{"colony", "replace"} {
		if _, err = h.Call(ctx, "manual-wall-"+action, "test/deconstruct_target", map[string]any{"target": target, "action": action}); err != nil {
			return err
		}
	}
	inspect := func(label string) (map[string]any, error) {
		return h.Call(ctx, label, "test/deconstruct_target", map[string]any{"target": target, "action": "inspect"})
	}
	before, err := inspect("manual-wall-readback")
	if err != nil {
		return err
	}
	present, _ := na.AsBool(before["present"])
	designated, _ := na.AsBool(before["designated"])
	if !present || !designated {
		return fmt.Errorf("manual player designation absent: %v", before)
	}
	grant, err := na.GrantAuto(ctx, h.WireFunc(), "takeover-auto", identity)
	if err != nil {
		return err
	}
	attempt := map[string]any{"controllerSessionId": "takeover-wall", "actionId": "adopt", "attemptId": "1"}
	request := map[string]any{"precondition": map[string]any{"identity": identity, "expectedGeneration": fmt.Sprint(na.GrantGeneration(grant)), "attempt": attempt}, "operation": map[string]any{"deconstruct": map[string]any{"target": map[string]any{"entityId": target}}}}
	reply, err := h.Wire(ctx, "auto-adopt-wall", "operations_execute", request)
	if err != nil {
		return err
	}
	_, receipt, err := na.Outcome(reply, "receipt")
	if err != nil {
		return err
	}
	lookup := map[string]any{"identity": identity, "attempt": attempt}
	if err = takeover.Receipt(ctx, h, lookup, receipt, s.Report()); err != nil {
		return err
	}
	for advanced := 0; advanced < 6000; advanced += 300 {
		if _, err = s.Advance(ctx, 300); err != nil {
			return err
		}
		reply, err := h.Wire(ctx, fmt.Sprintf("wall-progress-%d", advanced), "receipts_observe_progress", lookup)
		if err != nil {
			return err
		}
		_, progress, err := na.Outcome(reply, "progress")
		if err != nil {
			return err
		}
		if failed, ok := progress["unsuccessful"]; ok {
			return fmt.Errorf("adopted demolition failed: %v", failed)
		}
		if completed, ok := na.AsMap(progress["completed"]); ok {
			evidence, _ := na.AsMap(completed["evidence"])
			effect, _ := na.AsMap(evidence["deconstruct"])
			demolished, _ := na.AsBool(effect["demolitionObserved"])
			if !demolished || len(na.AsSlice(effect["workerIds"])) == 0 || effect["targetId"] != target {
				return fmt.Errorf("no exact native pawn demolition evidence: %v", effect)
			}
			after, err := inspect("auto-wall-readback")
			if err != nil {
				return err
			}
			present, known := na.AsBool(after["present"])
			if !known || present {
				return fmt.Errorf("adopted wall still present: %v", after)
			}
			s.Report()["demolition_effect"] = effect
			return nil
		}
	}
	return fmt.Errorf("adopted wall did not finish within 6000 ticks")
}
