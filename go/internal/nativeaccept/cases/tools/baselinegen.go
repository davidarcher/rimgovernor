package tools

import (
	"context"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/variantgen"
)

// The tribal8 baseline's spec is na.BaselineStart (#2027). Any harness that
// loads sustained.BaselineSave generates it into profile/Saves on first use
// (Config.EnsureSave); this case regenerates it on demand, under the run's
// default profile (every installed DLC since #1260), and stamps the result as
// current.
func init() {
	cases.Register(cases.Case{
		Name: "tools/baselinegen",
		Scope: "Fixture generation: regenerates " + sustained.BaselineSave + " (LostTribe, eight colonists, seed " +
			na.BaselineStart.Seed + ") through the new-colony op under the run's profile.",
		Start:  cases.Scenario{Spec: na.BaselineStart},
		Budget: 15 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: func(ctx context.Context, s cases.Session) error {
			row := map[string]any{"spec": na.BaselineStart}
			s.Report()["generated"] = row
			if err := variantgen.SaveVariant(ctx, s.Harness(), s.Config().Root, s.Config().Headless, sustained.BaselineSave, row); err != nil {
				return err
			}
			return na.StampBaseline(s.Config().Root)
		},
	})
}
