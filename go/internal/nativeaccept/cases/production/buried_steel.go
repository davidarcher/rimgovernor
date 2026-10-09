package production

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// production/buried-steel mines compacted steel under a
// thick mountain roof. test/buried_steel raises a granite block east of the
// lab colonists with one two-cell steel deposit on the face (bordering open
// ground) and one buried five cells in, fogged. The steel
// runway target (recurring spend over the horizon) is short from zero,
// both deposits together hold less than it, so MaintainResource must mine the face deposit as
// supported_roof and tunnel to the buried one and mine it too.
//
// Why not a snapshot test: the roof-support verdict is native
// ExcavationSafety.Check reading the true map through fog, and the roof
// holding is vanilla physics; only a live game shows the roof stay up.
const buriedSteelOp = "test/buried_steel"

func init() {
	cases.Register(cases.Case{
		Name:        "production/buried-steel",
		Scope:       "The steel floor mines a face deposit and tunnels to a buried one under a thick rock roof; the steel runway goes positive, both deposits are mined and no roof collapses (#1075). Native: roof support through fog and vanilla collapse physics.",
		Start:       cases.Fixture{Op: buriedSteelOp, Args: map[string]any{"action": "prepare"}, On: cases.Lab{Colonists: 6}},
		RequiredOps: []string{buriedSteelOp},
		Quiet:       na.QuietRequired, QuietWorld: true,
		Serve:  &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Resource, routinefamily.Supply}, NativeTimeout: 15 * time.Second, Prefix: "buried-steel"},
		Budget: 14 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: func(ctx context.Context, s cases.Session) error { return runBuriedSteel(ctx, s, false) },
	})
	// The stockpile against the face deposit: ore is always mined,
	// and each face cell beside colony space is followed by a wall.
	cases.Register(cases.Case{
		Name:        "production/buried-steel-stockpile",
		Scope:       "production/buried-steel with the stockpile beside the face deposit: the face is still mined and replacement walls are queued on the mined face cells (#1133).",
		Start:       cases.Fixture{Op: buriedSteelOp, Args: map[string]any{"action": "prepare", "stockpile": "face"}, On: cases.Lab{Colonists: 6}},
		RequiredOps: []string{buriedSteelOp},
		Quiet:       na.QuietRequired, QuietWorld: true,
		Serve:  &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Resource, routinefamily.Supply}, NativeTimeout: 15 * time.Second, Prefix: "buried-steel-stockpile"},
		Budget: 14 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: func(ctx context.Context, s cases.Session) error { return runBuriedSteel(ctx, s, true) },
	})
}

func runBuriedSteel(ctx context.Context, s cases.Session, besideFace bool) error {
	report := s.Report()
	prepared := s.Prepared()
	report["fixture"] = prepared
	if ok, _ := na.AsBool(prepared["success"]); !ok {
		return fmt.Errorf("fixture: %v", prepared)
	}
	if fogged, _ := na.AsBool(prepared["buriedFogged"]); !fogged {
		return fmt.Errorf("the buried deposit is not fogged: %v", prepared)
	}
	_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
		// Three in-game days: a face deposit, a five-cell granite
		// corridor and a second deposit for three lab colonists.
		WatchConfig: sustainedfood.WatchConfig{Watch: 11 * time.Minute, Window: 180000, Concern: policy.MaintainResource},
		Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
			before, err := h.Call(ctx, "audit-before", buriedSteelOp, map[string]any{"action": "audit"})
			if err != nil {
				return err
			}
			report["before"] = before
			if na.AsNumber(before["steelOnMap"]) != 0 || na.AsNumber(before["openLeft"]) != 2 || na.AsNumber(before["buriedLeft"]) != 2 {
				return fmt.Errorf("fixture did not start from zero steel and two intact deposits: %v", before)
			}
			return nil
		},
		Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
			after, err := h.Call(ctx, "audit-after", buriedSteelOp, map[string]any{"action": "audit"})
			if err != nil {
				return err
			}
			report["after"] = after
			return buriedSteelVerdict(after, besideFace)
		},
	})
	return err
}

// buriedSteelVerdict is the case's assertion over the final audit.
func buriedSteelVerdict(after map[string]any, besideFace bool) error {
	var failures []string
	if besideFace && na.AsNumber(after["replacementWalls"]) != 2 {
		failures = append(failures, fmt.Sprintf("face beside the stockpile not walled: %v of 2 cells", after["replacementWalls"]))
	}
	if n := na.AsNumber(after["roofless"]) + na.AsNumber(after["collapsedRocks"]) + na.AsNumber(after["collapsing"]); n != 0 {
		failures = append(failures, fmt.Sprintf("roof collapse: roofless=%v collapsedRocks=%v collapsing=%v", after["roofless"], after["collapsedRocks"], after["collapsing"]))
	}
	if na.AsNumber(after["openLeft"]) != 0 {
		failures = append(failures, fmt.Sprintf("face deposit not mined: %v cells left", after["openLeft"]))
	}
	if na.AsNumber(after["buriedLeft"]) != 0 {
		failures = append(failures, fmt.Sprintf("buried deposit not mined: %v cells left", after["buriedLeft"]))
	}
	if na.AsNumber(after["steelStored"]) <= 0 {
		failures = append(failures, fmt.Sprintf("steel runway did not go positive: stored=%v onMap=%v", after["steelStored"], after["steelOnMap"]))
	}
	if len(failures) > 0 {
		return fmt.Errorf("%v (audit %v)", failures, after)
	}
	return nil
}
