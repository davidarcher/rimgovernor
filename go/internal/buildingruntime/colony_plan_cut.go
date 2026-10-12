package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// perimeterWall is the strength of the wall the perimeter cut is advised
// for: the hit points of the wall made of the stuff the colony would build it
// from (the stocked stuff with the most hit points per cost), else of the
// wood the layout builds with, and of the wood wall laid on light footing.
func perimeterWall(projection observation.ColonyProjection) policy.PerimeterWall {
	out := policy.PerimeterWall{HP: domain.Unknown[float64](), LightHP: domain.Unknown[float64]()}
	for _, d := range projection.Definitions {
		if d.Name != policy.PerimeterDefs().WallDef {
			continue
		}
		price, priced := defenseUnitPrice(projection, d)
		for _, option := range d.StuffOptions {
			hp, shown := option.Stats[bridge.StatMaxHitPoints]
			if !shown {
				continue
			}
			if option.Stuff == policy.PerimeterLightStuff {
				out.LightHP = domain.Known(hp)
			}
			if priced && option.Stuff == price.Stuff {
				out.HP = domain.Known(hp)
			}
		}
	}
	if _, known := out.HP.Value(); !known {
		out.HP = out.LightHP
	}
	return out
}

// logPerimeterCut writes the layout_plan rows of the advised routing cut
// (target perimeter_cut, #2973): whether it closes every route but the
// opening, and the thickness of each of its segments. Advice only: nothing
// reads the cut yet.
func logPerimeterCut(ctx context.Context, plan policy.LayoutPlan, survey policy.MapSurvey, wall policy.PerimeterWall, colonists int) {
	for _, d := range policy.PlanPerimeterCut(plan, survey, wall).Decisions {
		attrs := map[string]any{"colonists": colonists}
		for k, v := range d.Attrs {
			attrs[k] = v
		}
		telemetry.Decide(ctx, telemetry.Decision{Kind: "layout_plan", Component: "layout", Verdict: d.Verdict, Reason: d.Reason, Target: "perimeter_cut", Attrs: attrs})
	}
}
