package wall

import (
	"context"
	"fmt"
	"reflect"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{Name: "wall/deconstruct", Scope: "Generic Deconstruct refuses colony and player-designated targets, preserves replacement ownership, releases only owned work, rejects disappearance as completion, and observes a native pawn demolition with replay and lookup.",
		Start: cases.Fixture{Op: "test/deconstruct_prepare", On: cases.Save{Name: baselineSave}}, Budget: 3 * time.Minute, Run: runDeconstruct})
}

func runDeconstruct(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	identity := s.Identity()
	ids := na.AsSlice(s.Prepared()["ids"])
	if len(ids) != 6 {
		return fmt.Errorf("expected six fixture targets: %#v", s.Prepared())
	}
	operation := func(i int) map[string]any {
		return map[string]any{"deconstruct": map[string]any{"target": map[string]any{"entityId": ids[i]}}}
	}
	execute := func(label string, op map[string]any) (map[string]any, map[string]any, error) {
		grant, err := na.GrantAuto(ctx, h.WireFunc(), label+"-authority", identity)
		if err != nil {
			return nil, nil, err
		}
		request := map[string]any{"precondition": map[string]any{"identity": identity, "expectedGeneration": fmt.Sprint(na.GrantGeneration(grant)), "attempt": map[string]any{"controllerSessionId": "deconstruct-acceptance", "actionId": label, "attemptId": "1"}}, "operation": op}
		reply, err := h.Wire(ctx, label, "operations_execute", request)
		return request, reply, err
	}
	attempt := func(request map[string]any) map[string]any {
		pre, _ := na.AsMap(request["precondition"])
		return map[string]any{"identity": identity, "attempt": pre["attempt"]}
	}
	progress := func(label string, request map[string]any) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "receipts_observe_progress", attempt(request))
		if err != nil {
			return nil, err
		}
		_, p, err := na.Outcome(reply, "progress")
		return p, err
	}
	mutate := func(label string, i int, action string) (map[string]any, error) {
		return h.Call(ctx, label, "test/deconstruct_target", map[string]any{"target": ids[i], "action": action})
	}
	for _, i := range []int{0, 1} {
		_, reply, err := execute(fmt.Sprintf("refuse-%d", i), operation(i))
		if err != nil {
			return err
		}
		if _, _, err = na.Outcome(reply, "failure"); err != nil {
			return fmt.Errorf("protected target accepted: %w", err)
		}
	}
	requests := make(map[int]map[string]any)
	for _, i := range []int{2, 3, 4} {
		request, reply, err := execute(fmt.Sprintf("designate-%d", i), operation(i))
		if err != nil {
			return err
		}
		if _, _, err = na.Outcome(reply, "receipt"); err != nil {
			return err
		}
		requests[i] = request
	}
	if _, err := mutate("player-replacement", 2, "replace"); err != nil {
		return err
	}
	if _, err := mutate("external-disappearance", 4, "vanish"); err != nil {
		return err
	}
	for _, i := range []int{2, 4} {
		p, err := progress(fmt.Sprintf("unachieved-%d", i), requests[i])
		if err != nil {
			return err
		}
		if _, ok := na.AsMap(p["unsuccessful"]); !ok {
			return fmt.Errorf("replacement/disappearance credited: %#v", p)
		}
	}
	_, reply, err := execute("release", map[string]any{"releaseDeconstructions": map[string]any{}})
	if err != nil {
		return err
	}
	_, receipt, err := na.Outcome(reply, "receipt")
	if err != nil {
		return err
	}
	applied, _ := na.AsMap(receipt["applied"])
	observed, _ := na.AsMap(applied["observed"])
	released, _ := na.AsMap(observed["releaseDeconstructions"])
	if na.AsNumber(released["releasedCount"]) != 1 {
		return fmt.Errorf("release must remove only owned designation: %#v", reply)
	}
	for _, i := range []int{1, 2, 3} {
		row, err := mutate(fmt.Sprintf("after-release-%d", i), i, "inspect")
		if err != nil {
			return err
		}
		designated, _ := na.AsBool(row["designated"])
		present, _ := na.AsBool(row["present"])
		if !present || designated != (i != 3) {
			return fmt.Errorf("release altered player work or missed own designation: %#v", row)
		}
	}
	request, reply, err := execute("finish", operation(5))
	if err != nil {
		return err
	}
	_, receipt, err = na.Outcome(reply, "receipt")
	if err != nil {
		return err
	}
	again, err := h.Wire(ctx, "replay", "operations_execute", request)
	if err != nil {
		return err
	}
	if !reflect.DeepEqual(reply, again) {
		return fmt.Errorf("retry changed receipt")
	}
	lookup, err := h.Wire(ctx, "lookup", "receipts_lookup", attempt(request))
	if err != nil {
		return err
	}
	_, looked, err := na.Outcome(lookup, "receipt")
	if err != nil {
		return err
	}
	if !reflect.DeepEqual(receipt, looked) {
		return fmt.Errorf("lookup changed receipt")
	}
	for advanced := 0; advanced < 6000; advanced += 300 {
		if _, err = s.Advance(ctx, 300); err != nil {
			return err
		}
		p, err := progress(fmt.Sprintf("job-%d", advanced), request)
		if err != nil {
			return err
		}
		if failed, ok := p["unsuccessful"]; ok {
			return fmt.Errorf("native demolition unsuccessful: %#v", failed)
		}
		if completed, ok := na.AsMap(p["completed"]); ok {
			evidence, _ := na.AsMap(completed["evidence"])
			effect, _ := na.AsMap(evidence["deconstruct"])
			demolition, _ := na.AsBool(effect["demolitionObserved"])
			if !demolition || len(na.AsSlice(effect["workerIds"])) == 0 {
				return fmt.Errorf("completion lacks native job evidence: %#v", effect)
			}
			row, err := mutate("finished-native-target", 5, "inspect")
			if err != nil {
				return err
			}
			if present, _ := na.AsBool(row["present"]); present {
				return fmt.Errorf("completed target still present")
			}
			s.Report()["deconstruction_effect"] = effect
			return nil
		}
	}
	return fmt.Errorf("native deconstruction did not finish within 6000 ticks")
}
