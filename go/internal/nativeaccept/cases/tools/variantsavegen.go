// Package tools generates fixture saves through the production new-colony operation. Each
// run overwrites its variant under root/profile/Saves for sustained/matrix-* to load. Map
// generation does not verify the intended stressor, such as scarce wood; inspect colony
// facts before trusting a new variant.
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
				" for the sustained matrix (issue #1).",
			Start:  cases.Scenario{Spec: v.Start()},
			Budget: 8 * time.Minute,
			Crew:   cases.Crew{Size: 3}, Run: func(ctx context.Context, s cases.Session) error {
				row := map[string]any{"variant": v}
				s.Report()["generated"] = row
				return variantgen.SaveVariant(ctx, s.Harness(), s.Config().Root, s.Config().Headless, v.Save, row)
			},
		})
	}
}
