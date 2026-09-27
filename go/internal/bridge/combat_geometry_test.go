package bridge

import (
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	"google.golang.org/protobuf/proto"
)

func rescuePathAsk() *mp.CombatGeometryRequest {
	propose := &mp.CombatGeometryPropose{Role: &mp.CombatGeometryPropose_RescuePath{RescuePath: &mp.CombatRescuePath{To: combatCell(3, 30)}}}
	return CombatGeometryProposeAsk(pbIdentity(), propose, []string{"h0"}, "p0")
}

// TestCombatGeometryRescuePath covers the rescue_path role (#867): it
// needs a pawn and a to cell, and its route cells may repeat, stand on a
// door and be unstandable, unlike a ranked proposal.
func TestCombatGeometryRescuePath(t *testing.T) {
	if err := ValidateCombatGeometryRequest(rescuePathAsk()); err != nil {
		t.Fatal(err)
	}
	for name, edit := range map[string]func(*mp.CombatGeometryRequest){
		"no pawn":  func(v *mp.CombatGeometryRequest) { v.PawnId = nil },
		"no to":    func(v *mp.CombatGeometryRequest) { v.Propose.GetRescuePath().To = nil },
		"bad to":   func(v *mp.CombatGeometryRequest) { v.Propose.GetRescuePath().To = combatCell(-1, 0) },
		"too many": func(v *mp.CombatGeometryRequest) { v.Cells = make([]*c.Cell, CombatGeometryMaxCells) },
	} {
		v := rescuePathAsk()
		edit(v)
		if err := ValidateCombatGeometryRequest(v); err == nil {
			t.Errorf("%s: accepted", name)
		}
	}
	line := func(fire bool) []*mp.CombatSightLine {
		return []*mp.CombatSightLine{{HostileId: proto.String("h0"), Cover: proto.Float64(0), LineOfFire: proto.Bool(fire)}}
	}
	reply := &mp.CombatGeometry{Context: pbContext(), Proposed: []*mp.CombatGeometryCell{
		{Cell: combatCell(2, 29), Lines: line(false), Door: proto.Bool(true), Standable: proto.Bool(true)},
		{Cell: combatCell(3, 29), Lines: line(true), HostileLineOfFire: proto.Bool(true)},
		{Cell: combatCell(3, 29), Lines: line(true), HostileLineOfFire: proto.Bool(true)},
	}}
	if err := ValidateCombatGeometry(reply, rescuePathAsk()); err != nil {
		t.Fatalf("route refused: %v", err)
	}
	reply.Proposed[0].Lines = nil
	if err := ValidateCombatGeometry(reply, rescuePathAsk()); err == nil {
		t.Fatal("a route cell without its lines accepted")
	}
}
