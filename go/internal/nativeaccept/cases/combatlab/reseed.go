package combatlab

import (
	"context"
	"fmt"
	"slices"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name:        "combatlab/reseed",
		Scope:       "Post-load reseed (#2849): reseeding the game RNG under seed 7 reads the same next draws every time, even after ticks advanced the RNG between, and seed 8 reads different draws. The same-checkpoint-twice property is also visible across combatlab/metrics-* runs: pass -run-seed and compare the report's run_seed_draws and staged_digest.",
		Start:       cases.Lab{Colonists: 5},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Crew:        cases.Crew{Size: 3},
		Run: func(ctx context.Context, s cases.Session) error {
			h := s.Harness()
			a, err := Reseed(ctx, h, 7)
			if err != nil {
				return err
			}
			if _, err := h.Call(ctx, "reseed-advance", StageTool, map[string]any{"action": "tick", "ticks": 50}); err != nil {
				return err
			}
			b, err := Reseed(ctx, h, 7)
			if err != nil {
				return err
			}
			c, err := Reseed(ctx, h, 8)
			if err != nil {
				return err
			}
			if !slices.Equal(a, b) {
				return fmt.Errorf("seed 7 read draws %v then %v", a, b)
			}
			if slices.Equal(a, c) {
				return fmt.Errorf("seeds 7 and 8 read the same draws %v", a)
			}
			s.Report()["draws"] = map[string]any{"seed7": a, "seed7_again": b, "seed8": c}
			return nil
		},
	})
}
