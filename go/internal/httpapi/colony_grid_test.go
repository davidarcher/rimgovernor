package httpapi

import (
	"encoding/json"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestRoutineColonyGridJSON(t *testing.T) {
	b, err := json.Marshal(routineStatus(RoutineStatus{}))
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(b), `"colonyGrid":null`) {
		t.Fatalf("unknown grid must be null: %s", b)
	}
	grid := policy.ColonyGrid{Origin: domain.Cell{X: 40, Z: 50}, Pitch: 16, Axes: policy.ColonyGridAxes, Source: policy.ColonyGridFromStarter}
	b, err = json.Marshal(routineStatus(RoutineStatus{ColonyGrid: domain.Known(grid), Bounds: policy.Bounds{Width: 250, Height: 200}}))
	if err != nil {
		t.Fatal(err)
	}
	want := `"colonyGrid":{"origin":{"x":40,"z":50},"pitch":16,"axes":[{"x":1,"z":0},{"x":0,"z":1}],"source":"starter_shell","bounds":{"width":250,"height":200}}`
	if !strings.Contains(string(b), want) {
		t.Fatalf("missing %s in %s", want, b)
	}
}
