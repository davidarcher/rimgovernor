package quest

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func prepareWandererDefense(ctx context.Context, s cases.Session) (domain.Cell, error) {
	prepared, err := s.Harness().Call(ctx, "firing-cover-site", "test/guarded_construction_prepare", map[string]any{"siteCount": 1})
	if err != nil {
		return domain.Cell{}, err
	}
	rows := na.AsSlice(prepared["sites"])
	if ok, _ := na.AsBool(prepared["success"]); !ok || len(rows) != 1 {
		return domain.Cell{}, fmt.Errorf("no firing cover site: %#v", prepared)
	}
	site, _ := na.AsMap(rows[0])
	return domain.Cell{X: int32(na.AsNumber(site["x"])), Z: int32(na.AsNumber(site["z"]))}, nil
}

func proveWandererDefense(ctx context.Context, s cases.Session, service *na.ServiceProcess, st *store.Store, cell domain.Cell, answers func() (bool, bool, string, error)) error {
	before, err := st.LoadRoutineReview(ctx)
	if err != nil {
		return err
	}
	world := store.World{Colony: before.Snapshot.Colony, Load: before.Snapshot.Load, Map: before.Snapshot.Map}
	if _, exists, err := st.LoadDefenseLayout(ctx, world); err != nil || exists {
		return fmt.Errorf("expected no defense record: exists=%v err=%v", exists, err)
	}
	// Wait for subsequent reviews so refusal cannot pass merely because the
	// planner has not yet seen the newly submitted policy.
	wait := na.Wait{Ceiling: 2 * time.Minute, Stall: na.StallBudget(), Terminal: service.Exited}
	if err := na.WaitProgress(ctx, wait, func(ctx context.Context) (string, bool, error) {
		review, err := st.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		found, _, _, err := answers()
		if err != nil {
			return "", false, err
		}
		if found {
			return "", false, fmt.Errorf("wanderer answered without defense above raid threshold")
		}
		return na.Signature(review.Revision), review.Revision >= before.Revision+3, nil
	}); err != nil {
		return err
	}
	s.Report()["undefended_refused"] = true
	// One wooden barricade is a minimal firing-cover tier. Completion must
	// observe the actual building, not merely a blueprint or write receipt.
	response, status, err := service.API("POST", "/api/buildings/plans", map[string]any{
		"requestId": "wanderer-firing-cover", "expected": s.Identity(),
		"building": map[string]any{"defName": "Barricade", "x": cell.X, "z": cell.Z, "rotation": "north", "stuff": "WoodLog"},
	}, service.Token)
	if err != nil {
		return err
	}
	if status != 201 {
		return fmt.Errorf("firing cover submission: %d %#v", status, response)
	}
	plan := domain.PlanID(na.AsString(response["planId"]))
	_, err = service.WaitPlan(ctx, na.Wait{Ceiling: 3 * time.Minute, Stall: na.StallBudget()}, plan, func(state store.PlanState) (string, bool, error) {
		complete := len(state.Progress) > 0
		for _, progress := range state.Progress {
			stage := progress.View().Stage
			if stage == domain.Unsuccessful || stage == domain.Cancelled {
				return "", false, fmt.Errorf("firing cover ended %s", stage)
			}
			complete = complete && stage == domain.Completed
		}
		return na.PlanSignature(state), complete, nil
	})
	if err != nil {
		return err
	}
	if found, _, _, err := answers(); err != nil || found {
		return fmt.Errorf("letter answered before built tier recorded: found=%v err=%v", found, err)
	}
	record := store.DefenseLayoutRecord{World: world, Goal: "wanderer-firing-cover", Firing: []domain.Cell{{X: cell.X, Z: cell.Z + 1}}, Tiers: []store.DefenseTierRecord{{Name: policy.TierFiringLine, Built: true, Buildings: []store.DefenseBuilding{{Definition: "Barricade", Cell: cell, Rotation: domain.North, Stuff: "WoodLog"}}}}}
	if err := st.SaveDefenseLayout(ctx, record); err != nil {
		return err
	}
	s.Report()["built_firing_cover_plan"] = string(plan)
	return nil
}
