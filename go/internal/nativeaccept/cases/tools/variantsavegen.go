// Package tools holds the cases that produce fixtures rather than assert on
// them: tools/variantsavegen-<save> generates one save variant of issue
// #1's sustained matrix through a programmatic scenario start
// (ScenarioStartFixture's test/configure_start) and persists it under
// root/profile/Saves, where the sustained/matrix-<save> case loads it. A
// run regenerates the save even when one exists. The generated map is not
// checked against its intended stressor (that a "scarce wood" biome really
// has few trees): inspect colony facts before trusting a new variant.
package tools

import (
	"context"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/variantgen"
)

func init() {
	for _, v := range sustained.Variants {
		v := v
		cases.Register(cases.Case{
			Name: "tools/variantsavegen-" + sustained.Short(v.Save),
			Scope: "Fixture generation: scenario start " + v.Scenario + " seed " + v.Seed + " saved as " + v.Save +
				" for the sustained matrix (issue #1); requires a ScenarioStartFixture build.",
			Start:  cases.Scenario{Spec: v.Start()},
			Budget: 8 * time.Minute,
			Run: func(ctx context.Context, s cases.Session) error {
				row := map[string]any{"variant": v}
				s.Report()["generated"] = row
				return variantgen.SaveVariant(ctx, s.Harness(), s.Config().Root, s.Config().Headless, v.Save, row)
			},
		})
	}
}
