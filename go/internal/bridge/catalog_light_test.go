package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The lamps the plan weighs are read from the recorded defs (#2945, #2948):
// both draw 30 W; the standing lamp glows to radius 12 and costs 20 steel, the
// wall lamp to 11 and costs 15 (Buildings_Furniture.xml, ilspycmd), and the wall
// lamp is a wall-access station.
func TestLightModelReadsBothLampsFromTheDefs(t *testing.T) {
	stations := recordedStations(t)
	light := stations.Light
	if light.Standing.PowerW != 30 || light.Wall.PowerW != 30 {
		t.Errorf("draw %v / %v W, want 30 / 30", light.Standing.PowerW, light.Wall.PowerW)
	}
	if light.Standing.Radius != 12 || light.Wall.Radius != 11 {
		t.Errorf("radius %v / %v, want 12 / 11", light.Standing.Radius, light.Wall.Radius)
	}
	if light.Wall.Build <= 0 || light.Wall.Build >= light.Standing.Build {
		t.Errorf("build value wall %v, standing %v: 15 steel should be cheaper than 20", light.Wall.Build, light.Standing.Build)
	}
	if s, ok := stations.Station(policy.LightDefs().WallLampDef); !ok || s.Access != policy.AccessWall {
		t.Errorf("wall lamp station %+v, %v: want wall access", s, ok)
	}
}
