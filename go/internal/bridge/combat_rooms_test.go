package bridge

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// A frame's standing rooms (#897) decode as the pods tactic's rooms: a
// room whose cells fill its bounds with its doors; an L-shaped room (its
// count short of its bounds) is left out.
func TestDecodeCombatRooms(t *testing.T) {
	frame := &o.BundleSnapshot{Context: authorityTestContext(7), CombatRooms: []*mp.CombatRoom{
		{RoomId: proto.String("3"), Min: combatCell(45, 55), Max: combatCell(55, 65), CellCount: proto.Uint32(121), Doors: []*c.Cell{combatCell(50, 54)}},
		{RoomId: proto.String("4"), Min: combatCell(10, 10), Max: combatCell(13, 13), CellCount: proto.Uint32(12)},
	}}
	combat, err := DecodeCombat(frame)
	if err != nil {
		t.Fatal(err)
	}
	want := []policy.CombatRoom{{Interior: policy.Rectangle{X: 45, Z: 55, Width: 11, Height: 11}, Doors: []domain.Cell{{X: 50, Z: 54}}}}
	if !reflect.DeepEqual(combat.Rooms, want) {
		t.Fatalf("%+v", combat.Rooms)
	}
	if got := combatFrame(frame); len(got.CombatRooms) != 2 {
		t.Fatal("the combat read drops the frame's rooms")
	}
}

// Named cells alone may be asked with no hostile: their standability
// before drop pods open (#897); a propose still needs one.
func TestCombatGeometryNamedCellsWithoutHostiles(t *testing.T) {
	ask := CombatGeometryAsk(pbIdentity(), []*c.Cell{combatCell(1, 1)}, nil, "")
	if err := ValidateCombatGeometryRequest(ask); err != nil {
		t.Fatal(err)
	}
	ask.Propose = &mp.CombatGeometryPropose{Role: &mp.CombatGeometryPropose_CoverBehindLine{CoverBehindLine: &mp.CombatCoverBehindLine{Line: []*c.Cell{combatCell(2, 2)}}}}
	if err := ValidateCombatGeometryRequest(ask); err == nil {
		t.Fatal("a propose without hostiles accepted")
	}
}
