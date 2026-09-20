package mood

import (
	"context"
	"fmt"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"time"
)

func init() {
	cases.Register(cases.Case{Name: "mood/subdue", Scope: "Ordinary melee containment: refuse non-aggro targets and ranged weapons; draft, issue, replay, lookup and observe a living downed or recovered colonist without prisoner conversion.", Start: cases.Fixture{Op: "test/subdue_prepare"}, Budget: 2 * time.Minute, Run: runSubdue})
}
func runSubdue(ctx context.Context, s cases.Session) error {
	h, identity, prepared := s.Harness(), s.Identity(), s.Prepared()
	pawn, target := na.AsString(prepared["pawn"]), na.AsString(prepared["target"])
	for _, scenario := range []string{"normal", "ranged", "legal"} {
		if _, err := h.Call(ctx, "stage-"+scenario, "test/subdue_stage", map[string]any{"pawnId": pawn, "targetId": target, "scenario": scenario}); err != nil {
			return err
		}
		grant, err := na.GrantAuto(ctx, h.WireFunc(), "grant-"+scenario, identity)
		if err != nil {
			return err
		}
		read := func(label string) (map[string]any, map[string]any, error) {
			reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{"scope": map[string]any{"expectedIdentity": identity}, "filter": map[string]any{"ids": []string{pawn, target}}})
			if err != nil {
				return nil, nil, err
			}
			a, err := na.PawnRow(reply, identity, pawn)
			if err != nil {
				return nil, nil, err
			}
			b, err := na.PawnRow(reply, identity, target)
			return a, b, err
		}
		a, b, err := read("before-" + scenario)
		if err != nil {
			return err
		}
		operation := map[string]any{"pawnTargetOrder": map[string]any{"pawn": na.Target(a), "target": na.Target(b), "kind": "PAWN_ORDER_KIND_SUBDUE", "requireSafeStorage": false}}
		preview, err := h.Wire(ctx, "preview-"+scenario, "operations_preview", map[string]any{"identity": identity, "operation": operation})
		if err != nil {
			return err
		}
		after, _, err := read("after-preview-" + scenario)
		if err != nil {
			return err
		}
		if err = na.SameControl(a, after); err != nil {
			return err
		}
		pre := map[string]any{"identity": identity, "expectedGeneration": fmt.Sprint(na.GrantGeneration(grant)), "attempt": map[string]any{"controllerSessionId": na.Controller, "actionId": "subdue-" + scenario, "attemptId": "1"}}
		request := map[string]any{"operation": operation, "precondition": pre}
		executed, err := h.Wire(ctx, "execute-"+scenario, "operations_execute", request)
		if err != nil {
			return err
		}
		if scenario != "legal" {
			if _, ok := preview["failure"]; !ok {
				return fmt.Errorf("%s preview accepted: %#v", scenario, preview)
			}
			if _, ok := executed["failure"]; !ok {
				return fmt.Errorf("%s execution accepted: %#v", scenario, executed)
			}
			after, _, err = read("after-refusal-" + scenario)
			if err != nil {
				return err
			}
			if err = na.SameControl(a, after); err != nil {
				return err
			}
			continue
		}
		_, receipt, err := na.Outcome(executed, "receipt")
		if err != nil {
			return err
		}
		if _, ok := receipt["applied"]; !ok {
			return fmt.Errorf("subdue not applied: %#v", receipt)
		}
		attempt := map[string]any{"identity": identity, "attempt": pre["attempt"]}
		for _, check := range []struct {
			label, tool string
			body        map[string]any
		}{{"replay", "operations_execute", request}, {"lookup", "receipts_lookup", attempt}} {
			got, err := h.Wire(ctx, check.label, check.tool, check.body)
			if err != nil {
				return err
			}
			_, r, err := na.Outcome(got, "receipt")
			if err != nil {
				return err
			}
			if _, ok := r["applied"]; !ok {
				return fmt.Errorf("%s lost receipt: %#v", check.label, r)
			}
		}
		completed, err := na.ObserveCompleted(ctx, h, "subdue-complete", 6000, attempt)
		if err != nil {
			return err
		}
		s.Report()["completed"] = completed
		facts, err := h.Call(ctx, "postcondition", "test/subdue_inspect", map[string]any{"targetId": target})
		if err != nil {
			return err
		}
		alive, _ := na.AsBool(facts["alive"])
		downed, _ := na.AsBool(facts["downed"])
		aggro, known := na.AsBool(facts["aggro"])
		prisoner, pk := na.AsBool(facts["prisoner"])
		if !alive || !known || !pk || prisoner || !downed && aggro {
			return fmt.Errorf("containment failed: %#v", facts)
		}
		s.Report()["containment"] = facts
	}
	return nil
}
