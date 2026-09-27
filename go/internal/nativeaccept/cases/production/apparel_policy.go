package production

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{Name: "production/apparel-policy", Scope: "The ApparelPolicyIntent creates, assigns and updates a role policy, reapplies as applied, and overrides manual policies and forced/locked apparel under autonomous control. A Go test cannot see the native filter write.", Start: cases.LabStart(), Budget: 2 * time.Minute, Run: runApparelPolicy})
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
	apply := func(label string, row map[string]any, hp float64, defs []string) error {
		intent := map[string]any{"pawnId": row["pawn"], "name": "RimGovernor worker", "allowedDefs": defs, "minHitPoints": hp, "maxHitPoints": 1, "minQuality": 1, "maxQuality": 6}
		reply, err := h.Wire(ctx, label, "operations_apply", map[string]any{"identity": identity, "actions": []any{map[string]any{"key": label, "apparelPolicy": intent}}})
		if err != nil {
			return err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return fmt.Errorf("%s: expected one result: %v", label, reply)
		}
		result, _ := na.AsMap(results[0])
		if _, ok := na.AsMap(result["applied"]); !ok {
			return fmt.Errorf("%s: apparel not applied: %v", label, reply)
		}
		return nil
	}
	defs := []string{"Apparel_BasicShirt", "Apparel_Pants"}
	for i, hp := range []float64{.51, .65, .65} {
		row := before
		if i > 0 {
			row, err = fixture("policy_read")
			if err != nil {
				return err
			}
			defs = []string{"Apparel_Pants"}
		}
		label := fmt.Sprintf("apparel-write-%d", i)
		if err := apply(label, row, hp, defs); err != nil {
			return err
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
		s.Report()[label] = after
	}
	edited, err := fixture("policy_edit")
	if err != nil {
		return err
	}
	if err := apply("apparel-repair-player-edit", edited, .51, []string{"Apparel_Pants"}); err != nil {
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
