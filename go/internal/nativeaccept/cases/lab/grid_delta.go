package lab

import (
	"context"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// gridDeltaUnchanged are the cell grid arrays one wall on open soil never
// changes: its delta must not carry them.
var gridDeltaUnchanged = []string{"cell", "zone", "roofed", "supports_light", "polluted", "glow", "roof", "zone_id", "terrain", "foundation_affordances", "snow_depth"}

func init() {
	cases.Register(cases.Case{
		Name: "lab/grid-delta",
		Scope: "Whole-map cell grid delta (#1551): test/grid_delta reads the native grid, spawns one player wall, " +
			"reads it again and encodes the delta as the stream does; the delta carries only sparse arrays that " +
			"include the wall cell, never the arrays a wall cannot change, and is a small fraction of the keyframe. " +
			"A Go snapshot test cannot see the native encoder read the map.",
		Start:       cases.Lab{Colonists: 1},
		RequiredOps: []string{"test/grid_delta"},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Run: func(ctx context.Context, s cases.Session) error {
			center, _ := na.AsMap(s.Prepared()["center"])
			cx, cz := int(na.AsNumber(center["x"])), int(na.AsNumber(center["z"]))
			reply, err := s.Harness().Call(ctx, "grid-delta", "test/grid_delta", map[string]any{"cell": fmt.Sprintf("%d,%d", cx+6, cz+6)})
			if err != nil {
				return err
			}
			if ok, _ := na.AsBool(reply["success"]); !ok {
				return fmt.Errorf("grid_delta: %#v", reply)
			}
			s.Report()["grid_delta"] = reply
			forbidden := map[string]bool{}
			for _, f := range gridDeltaUnchanged {
				forbidden[f] = true
			}
			arrays := na.AsSlice(reply["arrays"])
			if len(arrays) == 0 {
				return fmt.Errorf("grid_delta: the wall changed no array: %#v", reply)
			}
			for _, a := range arrays {
				row, _ := na.AsMap(a)
				field := na.AsString(row["field"])
				if forbidden[field] {
					return fmt.Errorf("grid_delta: delta carries %s, which one wall does not change: %#v", field, row)
				}
				if na.AsString(row["form"]) != "Sparse" {
					return fmt.Errorf("grid_delta: %s is not sparse: %#v", field, row)
				}
				if wall, _ := na.AsBool(row["wallCell"]); !wall {
					return fmt.Errorf("grid_delta: %s omits the wall cell: %#v", field, row)
				}
			}
			if key, delta := na.AsNumber(reply["keyframeBytes"]), na.AsNumber(reply["deltaBytes"]); delta*20 > key {
				return fmt.Errorf("grid_delta: delta %v bytes against keyframe %v", delta, key)
			}
			return nil
		},
	})
}
