package production

import (
	"context"
	"fmt"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"time"
)

func init() {
	cases.Register(cases.Case{Name: "production/apparel-policy", Scope: "SetApparelPolicy creates, assigns and updates a role policy, rejects stale CAS, and overrides manual policies and forced/locked apparel under autonomous control.", Start: cases.DebugStart{}, Budget: 2 * time.Minute, Run: runApparelPolicy})
}
func runApparelPolicy(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	identity := s.Identity()
	fixture := func(mode string) (map[string]any, error) {
		return h.Call(ctx, mode, "test/gear_fixture", map[string]any{"mode": mode})
	}
	before, err := fixture("policy_setup")
	if err != nil {
		return err
	}
	if _, err = na.GrantAuto(ctx, h.WireFunc(), "apparel-auto", identity); err != nil {
		return err
	}
	readGeneration := func() (any, error) {
		reply, err := h.Wire(ctx, "apparel-authority", "authority_read_status", map[string]any{"identity": identity})
		if err != nil {
			return nil, err
		}
		_, status, err := na.Outcome(reply, "status")
		c, _ := na.AsMap(status["context"])
		return c["nativeGeneration"], err
	}
	operation := func(row map[string]any, hp float64, defs []string) map[string]any {
		return map[string]any{"setApparelPolicy": map[string]any{"pawn": map[string]any{"entityId": row["pawn"], "expectedSnapshotToken": row["token"]}, "name": "RimGovernor worker", "allowedDefs": defs, "minHitPoints": hp, "maxHitPoints": 1, "minQuality": 1, "maxQuality": 6}}
	}
	defs := []string{"Apparel_BasicShirt", "Apparel_Pants"}
	op := operation(before, .51, defs)
	preview, err := h.Wire(ctx, "apparel-preview", "operations_preview", map[string]any{"identity": identity, "operation": op})
	if err != nil {
		return err
	}
	_, evaluation, err := na.Outcome(preview, "evaluated")
	if err != nil {
		return err
	}
	if ok, _ := na.AsBool(evaluation["accepted"]); !ok {
		return fmt.Errorf("apparel preview refused: %v", preview)
	}
	unchanged, err := fixture("policy_read")
	if err != nil {
		return err
	}
	if unchanged["policy"] != before["policy"] || unchanged["count"] != before["count"] || unchanged["forced"] != before["forced"] {
		return fmt.Errorf("preview mutated policy: %v", unchanged)
	}
	execute := func(label string, op map[string]any) (map[string]any, error) {
		g, err := readGeneration()
		if err != nil {
			return nil, err
		}
		return h.Wire(ctx, label, "operations_execute", map[string]any{"precondition": map[string]any{"identity": identity, "expectedGeneration": g, "attempt": map[string]any{"controllerSessionId": "apparel-policy-smoke", "actionId": label, "attemptId": "1"}}, "operation": op})
	}
	for i, hp := range []float64{.51, .65} {
		row := before
		if i > 0 {
			row, err = fixture("policy_read")
			if err != nil {
				return err
			}
			defs = []string{"Apparel_Pants"}
		}
		label := fmt.Sprintf("apparel-write-%d", i)
		reply, err := execute(label, operation(row, hp, defs))
		if err != nil {
			return err
		}
		_, receipt, err := na.Outcome(reply, "receipt")
		if err != nil {
			return err
		}
		if _, ok := na.AsMap(receipt["applied"]); !ok {
			return fmt.Errorf("apparel not applied: %v", reply)
		}
		after, err := fixture("policy_read")
		if err != nil {
			return err
		}
		forced, _ := after["forced"].(float64)
		locked, _ := na.AsBool(after["locked"])
		tainted, _ := na.AsBool(after["tainted"])
		clean, _ := na.AsBool(after["clean"])
		minHP, _ := after["minHP"].(float64)
		actualDefs, _ := after["defs"].([]any)
		if na.AsString(after["name"]) != "RimGovernor worker" || forced != 0 || locked || tainted || !clean || minHP < hp-.00001 || minHP > hp+.00001 || len(actualDefs) != len(defs) || after["minQuality"] != float64(1) || after["maxQuality"] != float64(6) {
			return fmt.Errorf("native filter/assignment mismatch: %v", after)
		}
		for j, d := range defs {
			if actualDefs[j] != d {
				return fmt.Errorf("definition mismatch: %v", actualDefs)
			}
		}
		if i > 0 && after["policy"] != row["policy"] {
			return fmt.Errorf("update created another policy")
		}
		progress, err := h.Wire(ctx, label+"-progress", "receipts_observe_progress", map[string]any{"identity": identity, "attempt": receipt["attempt"]})
		if err != nil {
			return err
		}
		_, p, err := na.Outcome(progress, "progress")
		if err != nil {
			return err
		}
		if _, ok := na.AsMap(p["completed"]); !ok {
			return fmt.Errorf("policy readback did not complete: %v", progress)
		}
		s.Report()[label] = after
	}
	stale, err := execute("apparel-stale", op)
	if err != nil {
		return err
	}
	if _, _, err = na.Outcome(stale, "failure"); err != nil {
		return fmt.Errorf("stale CAS accepted: %v", stale)
	}
	edited, err := fixture("policy_edit")
	if err != nil {
		return err
	}
	repaired, err := execute("apparel-repair-player-edit", operation(edited, .51, []string{"Apparel_Pants"}))
	if err != nil {
		return err
	}
	if _, _, err = na.Outcome(repaired, "receipt"); err != nil {
		return err
	}
	final, err := fixture("policy_read")
	if err != nil {
		return err
	}
	if tainted, _ := na.AsBool(final["tainted"]); tainted {
		return fmt.Errorf("manual tainted edit not overridden")
	}
	s.Report()["autonomous_override"] = final
	return nil
}
