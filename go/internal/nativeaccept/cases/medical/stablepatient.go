// Package medical holds issue #1's stable-patient diagnostic: a fresh debug
// game seeded with the disposable test/medical_management_setup fixture
// (two tendable Flu patients plus a fourth colonist forced into
// GoJuiceAddiction's withdrawal stage) and test/routine_production_prepare
// (a pre-grown rice zone and fueled campfire bill), the live service with
// both the tend/medical and food routine families on, and the durable
// store's CriticalMedicine and EnsureFoodSupply goal-state timelines over a
// wall-clock window -- evidence for whether ordinary triage/feeding and
// concurrent crop-replacement production progress together rather than one
// starving the other of pawn time. A diagnostic, not a pass/fail gate: it
// fails only when the harness itself could not complete.
package medical

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// window is the observation window; a diagnostic samples for the whole of it.
const window = 8 * time.Minute

func init() {
	cases.Register(cases.Case{
		Name: "medical/stable-patient",
		Scope: "Diagnostic: CriticalMedicine (two Flu patients plus a forced GoJuiceAddiction " +
			"withdrawal patient) and EnsureFoodSupply (concurrent pre-seeded growing zone/campfire bill) goal-state " +
			"timelines under the live routine reviewer/field planner, evidence for issue #1's stable-patient feeding " +
			"acceptance extension to withdrawal recovery and concurrent food production. Not a pass/fail acceptance gate.",
		Start: cases.Fixture{
			Op:   "test/medical_management_setup",
			Args: map[string]any{"disease": true, "failSurgery": false, "manualTending": false, "withdrawal": true},
		},
		// Feeding the patients and resting through tending are what the
		// timeline is about.
		Keep: []string{string(na.NeedFood), string(na.NeedRest)},
		// Medical: tend for the two Flu patients and the forced withdrawal
		// patient's CriticalMedicine deficit, plus medicine-reserve
		// replenishment so tend never runs the fixture's stocked medicine
		// dry. Food: the same EnsureFoodSupply pipeline sustained/food
		// exercises, to prove the pre-seeded growing zone/campfire bill
		// keeps advancing concurrently with medical dispatch.
		Serve: &cases.ServeSpec{
			Families:      []string{"tend,medical,field,food-storage,acquisition,cooking,supply,production-policy"},
			NativeTimeout: 15 * time.Second, Prefix: "stable-patient",
		},
		Budget: 15 * time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			medicalPrepared := s.Prepared()
			s.Report()["medical_prepared"] = medicalPrepared
			if na.AsString(medicalPrepared["withdrawalPatient"]) == "" {
				return fmt.Errorf("medical_management_setup: missing withdrawalPatient identifier: %#v", medicalPrepared)
			}
			_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
				WatchConfig: sustainedfood.WatchConfig{Watch: window, Poll: 5 * time.Second, Goal: policy.CriticalMedicine, Extra: []policy.GoalID{policy.EnsureFoodSupply}},
				Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
					productionPrepared, err := h.Call(ctx, "production-setup", "test/routine_production_prepare", map[string]any{})
					if err != nil {
						return err
					}
					if success, _ := na.AsBool(productionPrepared["success"]); !success {
						return fmt.Errorf("routine_production_prepare refused: %#v", productionPrepared)
					}
					report["production_prepared"] = productionPrepared
					return nil
				},
				// Both goals' final state, side by side, for the summary.
				Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
					journal, err := na.OpenStoreWithRetry(ctx, s.Config().Output+"/service.sqlite")
					if err != nil {
						return err
					}
					defer journal.Close()
					food, err := sustainedfood.SampleGoal(ctx, journal, policy.EnsureFoodSupply)
					if err != nil {
						return err
					}
					report["food_final"] = food
					medical, err := sustainedfood.SampleGoal(ctx, journal, policy.CriticalMedicine)
					if err != nil {
						return err
					}
					report["medical_final"] = medical
					report["concurrent_progress"] = map[string]any{
						"medical_methods": medical["method_count"], "medical_need": medical["need"],
						"food_methods": food["method_count"], "food_need": food["need"],
					}
					return nil
				},
			})
			return err
		},
	})
}
