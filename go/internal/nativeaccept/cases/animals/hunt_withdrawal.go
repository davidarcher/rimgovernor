package animals

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name:        "animals/hunt-withdrawal",
		Scope:       "Withdraw a dispatched hunt after its prey moves and authority changes; remove the designation and active Hunt job, reconcile the original and withdrawal attempts, then admit a replacement hunt.",
		Start:       cases.Fixture{Op: "test/apply_refusal_prepare"},
		RequiredOps: []string{"test/apply_refusal_move"},
		Budget:      2 * time.Minute,
		Run:         huntWithdrawal,
	})
}

func huntWithdrawal(ctx context.Context, s cases.Session) error {
	h, identity, prepared := s.Harness(), s.Identity(), s.Prepared()
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "grant", identity); err != nil {
		return err
	}
	target := map[string]any{"source": map[string]any{"entityId": prepared["preyId"]}, "resourceDefName": prepared["preyResource"], "cell": prepared["preyCell"]}
	attempt := func(action string, n int) map[string]any {
		return map[string]any{"controllerSessionId": "hunt-withdrawal", "actionId": action, "attemptId": fmt.Sprint(n)}
	}
	execute := func(label, action string, n int, kind string, value map[string]any) (map[string]any, error) {
		_, generation, err := na.AuthorityStatus(ctx, h.WireFunc(), label+"-authority", identity)
		if err != nil {
			return nil, err
		}
		return h.Wire(ctx, label, "operations_execute", map[string]any{
			"precondition": map[string]any{"identity": identity, "expectedGeneration": fmt.Sprint(generation), "attempt": attempt(action, n)},
			"operation":    map[string]any{kind: value},
		})
	}
	requireApplied := func(reply map[string]any, err error) error {
		if err != nil {
			return err
		}
		_, receipt, err := na.Outcome(reply, "receipt")
		if err != nil {
			return err
		}
		if _, ok := na.AsMap(receipt["applied"]); !ok {
			return fmt.Errorf("expected applied receipt: %#v", receipt)
		}
		return nil
	}
	if err := requireApplied(execute("designate", "hunt", 1, "acquireResource", target)); err != nil {
		return err
	}
	inspect := func(action string) (map[string]any, error) {
		return h.Call(ctx, action, "test/apply_refusal_move", map[string]any{"action": action, "id": prepared["preyId"]})
	}
	started, err := inspect("start_hunt")
	if err != nil {
		return err
	}
	if ok, _ := na.AsBool(started["success"]); !ok || na.AsNumber(started["hunters"]) != 1 {
		return fmt.Errorf("fixture did not stage an active hunt: %#v", started)
	}
	// The renewed grant models the authority boundary after a safety stop.
	if _, err = na.GrantAuto(ctx, h.WireFunc(), "regrant", identity); err != nil {
		return err
	}
	// An unrelated action cannot withdraw another action's hunt.
	foreign, err := execute("foreign-withdrawal", "foreign", 2, "cancelAcquisition", target)
	if err != nil {
		return err
	}
	if _, _, err = na.Outcome(foreign, "failure"); err != nil {
		return fmt.Errorf("foreign hunt withdrawal was not refused: %w", err)
	}
	if err = requireApplied(execute("withdraw", "hunt", 2, "cancelAcquisition", target)); err != nil {
		return err
	}
	observed, err := inspect("inspect_hunt")
	if err != nil {
		return err
	}
	if designated, _ := na.AsBool(observed["designated"]); designated || na.AsNumber(observed["hunters"]) != 0 {
		return fmt.Errorf("withdrawal left native hunt work alive: %#v", observed)
	}
	for _, n := range []int{1, 2} {
		reply, err := h.Wire(ctx, fmt.Sprintf("observe-%d", n), "receipts_observe_progress", map[string]any{"identity": identity, "attempt": attempt("hunt", n)})
		if err != nil {
			return err
		}
		_, progress, err := na.Outcome(reply, "progress")
		if err != nil {
			return err
		}
		unsuccessful, ok := na.AsMap(progress["unsuccessful"])
		if complete, _ := na.AsBool(progress["completeInspection"]); !ok || !complete || na.AsString(unsuccessful["reason"]) != "UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED" {
			return fmt.Errorf("hunt attempt %d did not settle: %#v", n, progress)
		}
	}
	target["cell"] = started["cell"]
	if err = requireApplied(execute("replacement", "replacement", 1, "acquireResource", target)); err != nil {
		return err
	}
	s.Report()["withdrawn_hunt"] = observed
	return nil
}
