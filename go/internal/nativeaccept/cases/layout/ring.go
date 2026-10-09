package layout

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"slices"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/startersite"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// expansionMethod starts every MaintainHousing expansion-phase building
// method (buildingruntime's capacity selection).
const expansionMethod = "expansion-"

func init() {
	cases.Register(cases.Case{
		Name: "layout/ring",
		Scope: "Issue #1271: on the tribal " + sustained.BaselineSave + " colony at Masonry, staged one review short of " +
			"MaintainHousing's expansion phase (a bedroom per single colonist and per couple, a used bed each, no spare place, Foothold's exits met, the food gap pinned to zero), " +
			"the expansion planner raises the capacity ring: the native audit reads a stone Door and stone-block walls. " +
			"A snapshot test cannot cover it: the ring is proven by native construction of the tier's stuff, and the run " +
			"records the step read TestLayoutRingStepIsMasonry replays.",
		Start: cases.Fixture{Op: gridPrepare, ArgsFrom: startersite.BedroomArgs,
			Args: map[string]any{"sleepingSpots": 8, "stoneBlocks": blocks, "builders": true, "expansion": true},
			On:   cases.Save{Name: sustained.BaselineSave}},
		RequiredOps: []string{gridAudit},
		Keep:        []string{string(na.NeedFood)},
		// The start builds no food economy: the food plan's gap is pinned
		// to zero so the expansion gate opens.
		Serve: &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Shelter, routinefamily.Expansion, routinefamily.Sleeping}, NativeTimeout: 30 * time.Second, Prefix: "layout-ring",
			Env: []string{buildingruntime.FaultsEnv + "=foodgap=zero"}},
		Budget: 3 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Reason: "one watch: the fixture raises the ring the expansion step orders",
		Run: ring,
	})
}

func ring(ctx context.Context, s cases.Session) error {
	report := s.Report()
	report["fixture"] = s.Prepared()
	_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
		WatchConfig: sustainedfood.WatchConfig{Watch: 2 * time.Minute, Extra: []policy.ConcernID{policy.MaintainHousing}, Until: func(sample map[string]any) bool {
			goal, _ := sample[string(policy.MaintainHousing)].(map[string]any)
			return planned(goal, func(m domain.MethodID) bool { return strings.HasPrefix(string(m), expansionMethod) }, true)
		}},
		Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
			audit, err := h.Call(ctx, "ring-audit", gridAudit, map[string]any{})
			if err != nil {
				return err
			}
			doors, walls := stoneRing(audit)
			report["ring"] = map[string]any{"stoneDoors": doors, "stoneWalls": walls}
			if doors == 0 || walls == 0 {
				return fmt.Errorf("no stone ring stands: %d stone doors, %d stone-block walls", doors, walls)
			}
			return nil
		},
	})
	return err
}

// stoneRing counts the audit's stone-block Doors and Walls, standing or
// planned: the watch ends once the step is planned, so the ring may still
// be blueprints and frames, whose planned rows carry the stuff each becomes.
func stoneRing(audit map[string]any) (doors, walls int) {
	for _, item := range append(na.AsSlice(audit["walls"]), na.AsSlice(audit["planned"])...) {
		row, _ := na.AsMap(item)
		if !slices.Contains(policy.CoreItemFacts().Categories[policy.Resource(na.AsString(row["stuff"]))], "StoneBlocks") {
			continue
		}
		switch na.AsString(row["def"]) {
		case "Door":
			doors++
		case "Wall":
			walls++
		}
	}
	return doors, walls
}
