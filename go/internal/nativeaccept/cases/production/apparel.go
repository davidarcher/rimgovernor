package production

import (
	"context"
	"fmt"
	"path/filepath"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The apparel case (issue #233): one colonist wears a cloth shirt at 30%
// condition, no loose apparel exists, a hand tailoring bench stands with
// ComplexClothing finished, and the only fabric in stock is plain leather.
// MaintainEquipment must raise a shirt bill on that bench from the leather
// (the inspected cloth is a preference, not a requirement) and, once the
// shirt lands, dress the colonist in it through the gear-replace admission.
// The watch ends when the wear order completes; the audit needs live
// evidence: the pawn's worn shirt above the tattered threshold.
const (
	apparelDefinition = "Apparel_BasicShirt"
	apparelMaterial   = "Leather_Plain"
	apparelBench      = "HandTailoringBench"
	apparelWindow     = 12 * time.Minute
)

// apparelFamilies: gear plans the bill and the wear order; work covers the
// bench's Tailoring work type.
const apparelFamilies = "work,gear"

func init() {
	cases.Register(cases.Case{
		Name:   "production/apparel",
		Scope:  fmt.Sprintf("MaintainEquipment replaces a tattered cloth %s from %s in stock: a bill on a %s produces it and the colonist is dressed in it (issue #233).", apparelDefinition, apparelMaterial, apparelBench),
		Start:  cases.Save{Name: sustained.BaselineSave},
		Keep:   []string{string(na.NeedFood)},
		Serve:  &cases.ServeSpec{Families: []string{apparelFamilies}, NativeTimeout: 15 * time.Second, Prefix: "apparel"},
		Budget: apparelWindow + 3*time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			var subject string
			_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
				WatchConfig: sustainedfood.WatchConfig{Watch: apparelWindow, Poll: 5 * time.Second, Goal: policy.MaintainEquipment, Until: apparelWorn},
				Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
					setup, err := h.Call(ctx, "gear-setup", "test/gear_fixture", map[string]any{"mode": "setup"})
					if err != nil {
						return err
					}
					production, err := h.Call(ctx, "gear-production-setup", "test/gear_fixture", map[string]any{"mode": "production_setup", "material": apparelMaterial})
					if err != nil {
						return err
					}
					research, err := h.Call(ctx, "gear-production-research", "test/gear_fixture", map[string]any{"mode": "production_research"})
					if err != nil {
						return err
					}
					report["fixture"] = map[string]any{"setup": setup, "production": production, "research": research}
					for _, reply := range []map[string]any{setup, production, research} {
						if ok, _ := na.AsBool(reply["success"]); !ok {
							return fmt.Errorf("gear fixture refused: %#v", reply)
						}
					}
					if na.AsString(production["material"]) != apparelMaterial {
						return fmt.Errorf("fixture placed %v, not %s", production["material"], apparelMaterial)
					}
					subject = na.AsString(setup["pawn"])
					worn, row, err := wornShirt(ctx, h, "baseline-gear", subject)
					report["baseline_gear"] = row
					if err != nil {
						return err
					}
					report["baseline_worn"] = worn
					if condition(worn) > policy.GearTatteredCondition {
						return fmt.Errorf("the fixture shirt is not tattered: %#v", worn)
					}
					return nil
				},
				Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
					journal, err := store.Open(ctx, filepath.Join(s.Config().Output, "service.sqlite"))
					if err != nil {
						return fmt.Errorf("reopen journal: %w", err)
					}
					defer journal.Close()
					return auditApparel(ctx, h, journal, report, subject)
				},
			})
			return err
		},
	})
}

// apparelWorn reports a sample whose MaintainEquipment goal holds or held a
// gear plan whose wear order (gear_replace) completed: the pawn was
// observed wearing the exact produced item.
func apparelWorn(sample map[string]any) bool {
	return gearPlanCompleted(sample, string(domain.GearReplaceAction))
}

func gearPlanCompleted(sample map[string]any, kind string) bool {
	plans, _ := sample["plans"].([]map[string]any)
	retired, _ := sample["retired_plans"].([]map[string]any)
	for _, plan := range append(plans, retired...) {
		id, _ := plan["plan"].(string)
		actions, _ := plan["actions"].(int)
		stages, _ := plan["stages"].(map[string]int)
		kinds, _ := plan["kinds"].(map[string]int)
		if strings.HasPrefix(id, "routine-gear-") && actions > 0 && stages["completed"] == actions && kinds[kind] > 0 {
			return true
		}
	}
	return false
}

// wornShirt reads the subject's worn apparel from the native gear
// inspection and returns the shirt's gear row and the pawn's whole row
// (worn, candidates, replacement needs, deficit) for the report.
func wornShirt(ctx context.Context, h *na.Harness, label, pawn string) (map[string]any, map[string]any, error) {
	reply, err := h.Call(ctx, label, "home/gear_upkeep", map[string]any{"pawn": pawn, "dryRun": true})
	if err != nil {
		return nil, nil, err
	}
	for _, raw := range na.AsSlice(reply["pawns"]) {
		row, _ := na.AsMap(raw)
		if na.AsString(row["pawn"]) != pawn {
			continue
		}
		for _, item := range na.AsSlice(row["worn"]) {
			worn, _ := na.AsMap(item)
			gear, _ := na.AsMap(worn["gear"])
			if na.AsString(gear["defName"]) == apparelDefinition {
				return gear, row, nil
			}
		}
		return nil, row, fmt.Errorf("pawn %s wears no %s: %#v", pawn, apparelDefinition, row["worn"])
	}
	return nil, nil, fmt.Errorf("pawn %s missing from the native gear inspection", pawn)
}

func condition(gear map[string]any) float64 {
	max := na.AsNumber(gear["maxHitPoints"])
	if max <= 0 {
		return 1
	}
	return na.AsNumber(gear["hitPoints"]) / max
}

// auditApparel compares the journal's gear methods with the live loadout
// after the service has stopped: a completed bill and a completed wear
// order under MaintainEquipment, and the subject wearing a shirt above the
// tattered threshold.
func auditApparel(ctx context.Context, h *na.Harness, journal *store.Store, report na.Report, subject string) error {
	review, err := journal.LoadRoutineReview(ctx)
	if err != nil {
		return fmt.Errorf("load routine review: %w", err)
	}
	var goalID domain.GoalID
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainEquipment {
			goalID = binding.Goal
		}
	}
	if goalID == "" {
		return fmt.Errorf("MaintainEquipment was never bound in the routine review")
	}
	goal, err := journal.LoadGoal(ctx, goalID)
	if err != nil {
		return err
	}
	kinds := map[domain.ActionKind]int{}
	if history, err := journal.LoadGoalMethods(ctx, goalID, goal.Goal.Epoch); err == nil {
		for _, method := range history {
			plan, err := journal.LoadPlan(ctx, method.Plan)
			if err != nil {
				continue
			}
			for _, p := range plan.Progress {
				if p.View().Stage == domain.Completed {
					kinds[p.Action().Kind()]++
				}
			}
		}
	}
	report["equipment_goal"] = map[string]any{"status": string(goal.Goal.Status), "need": string(goal.Goal.Need), "epoch": goal.Goal.Epoch, "completed": kinds}
	// The live loadout is recorded before the journal is judged so a failed
	// run still shows what the pawn wears and what the census offered.
	worn, row, wornErr := wornShirt(ctx, h, "final-gear", subject)
	report["final_gear"] = row
	report["final_worn"] = worn
	if kinds[domain.ProductionBillAction] == 0 {
		return fmt.Errorf("no completed %s under MaintainEquipment: %v", domain.ProductionBillAction, kinds)
	}
	if kinds[domain.GearReplaceAction] == 0 {
		return fmt.Errorf("no completed %s under MaintainEquipment: %v", domain.GearReplaceAction, kinds)
	}
	if wornErr != nil {
		return wornErr
	}
	if condition(worn) <= policy.GearTatteredCondition || na.AsString(worn["stuff"]) != apparelMaterial {
		return fmt.Errorf("pawn %s does not wear the produced %s %s: %#v", subject, apparelMaterial, apparelDefinition, worn)
	}
	return nil
}
