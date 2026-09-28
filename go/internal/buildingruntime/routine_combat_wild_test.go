package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	"google.golang.org/protobuf/proto"
)

// TestCombatPawnStatesWildSide (#1116): a COMBAT_SIDE_WILD_ANIMAL row,
// appended as 5, is a wild pawn, neither colony animal nor prisoner.
func TestCombatPawnStatesWildSide(t *testing.T) {
	if mp.CombatSide_COMBAT_SIDE_WILD_ANIMAL != 5 {
		t.Fatal("the wild side is appended as 5")
	}
	combat := bridge.Combat{Pawns: []*mp.CombatPawn{
		{Id: proto.String("Thrumbo_1"), Side: mp.CombatSide_COMBAT_SIDE_WILD_ANIMAL.Enum(), Cell: &c.Cell{X: proto.Int32(4), Z: proto.Int32(5)}, Health: proto.Float64(1)},
		{Id: proto.String("Human_1"), Side: mp.CombatSide_COMBAT_SIDE_COLONIST.Enum(), Cell: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}},
	}}
	got := combatPawnStates(combat, nil)
	if len(got) != 2 || !got[0].Wild || got[0].Animal || got[0].Prisoner || got[1].Wild {
		t.Fatalf("%+v", got)
	}
}
