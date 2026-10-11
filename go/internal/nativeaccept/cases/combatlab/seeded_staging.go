package combatlab

import (
	"context"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name:        "combatlab/seeded-staging",
		Scope:       "Seeded lab staging (#2833): lab-ranged staged twice under seed 7 reads back the same digest and echoes the seed, seed 8 reads a different digest (hostile skills vary), and seed 0 reads the unseeded staging's digest.",
		Start:       cases.Lab{Colonists: 5},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Crew:        cases.Crew{Size: 3},
		Run: func(ctx context.Context, s cases.Session) error {
			h := s.Harness()
			const name = "lab-ranged"
			stage := func(edits ...func(*Fixture, int, int)) (Staged, error) { return Stage(ctx, h, name, edits...) }
			plain, err := stage()
			if err != nil {
				return err
			}
			zero, err := stage(WithSeed(0))
			if err != nil {
				return err
			}
			a, err := stage(WithSeed(7))
			if err != nil {
				return err
			}
			b, err := stage(WithSeed(7))
			if err != nil {
				return err
			}
			c, err := stage(WithSeed(8))
			if err != nil {
				return err
			}
			if zero.Digest != plain.Digest {
				return fmt.Errorf("seed 0 digest %s differs from the unseeded %s: %s", zero.Digest, plain.Digest, rowDiff(plain.Rows, zero.Rows))
			}
			if a.Seed != 7 || c.Seed != 8 || zero.Seed != 0 {
				return fmt.Errorf("seeds echoed %d, %d, %d, want 7, 8, 0", a.Seed, c.Seed, zero.Seed)
			}
			if a.Digest != b.Digest {
				return fmt.Errorf("seed 7 stagings differ: %s", rowDiff(a.Rows, b.Rows))
			}
			if a.Digest == c.Digest {
				return fmt.Errorf("seeds 7 and 8 read the same digest %s", a.Digest)
			}
			s.Report()["digests"] = map[string]any{"unseeded": plain.Digest, "seed0": zero.Digest, "seed7": a.Digest, "seed8": c.Digest}
			return nil
		},
	})
}
