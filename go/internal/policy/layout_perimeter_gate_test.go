package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A gate on a step of a squared-off diagonal must lead outside: the next
// step's wall stands in the strip out from its door, and a door into that jog
// leads nowhere.
func TestStepGateIsKeptOnlyWhereItOpensOutside(t *testing.T) {
	step := func(face domain.Cell) stepGate {
		return stepGate{area: rectOf(face, domain.Cell{X: face.X, Z: face.Z + 2}), face: face, out: domain.Cell{X: 0, Z: -1}, along: domain.Cell{X: 1}}
	}
	open := step(domain.Cell{X: 10, Z: 10})
	jog := step(domain.Cell{X: 30, Z: 10})
	walls := map[domain.Cell]bool{{X: 31, Z: 8}: true, {X: 31, Z: 9}: true}
	blocked := func(c domain.Cell) bool { return walls[c] }
	got := pitchGates(nil, []stepGate{open, jog}, blocked)
	if len(got) != 1 || got[0] != open.area {
		t.Fatalf("kept %v, want only the gate that opens onto open ground", got)
	}
}
