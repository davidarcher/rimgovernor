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

// The frame's outdoor temperature (#1077) decodes as a known fact and
// survives the combat read; absent it is unknown; NaN or out of range is
// a contract failure.
func TestDecodeCombatOutdoorTemperature(t *testing.T) {
	frame := &o.BundleSnapshot{Context: authorityTestContext(7), CombatOutdoorTemperatureC: proto.Float32(-32.5)}
	combat, err := DecodeCombat(frame)
	if err != nil {
		t.Fatal(err)
	}
	if got, ok := combat.OutdoorTemperatureC.Value(); !ok || got != -32.5 {
		t.Fatalf("temperature %v %v", got, ok)
	}
	if combatFrame(frame).CombatOutdoorTemperatureC == nil {
		t.Fatal("the combat read drops the frame's outdoor temperature")
	}
	combat, err = DecodeCombat(&o.BundleSnapshot{Context: authorityTestContext(7)})
	if _, ok := combat.OutdoorTemperatureC.Value(); err != nil || ok {
		t.Fatal("an absent temperature is not unknown")
	}
	nan := float32(0)
	nan /= nan
	for _, bad := range []float32{nan, 1000} {
		if _, err := DecodeCombat(&o.BundleSnapshot{Context: authorityTestContext(7), CombatOutdoorTemperatureC: proto.Float32(bad)}); err == nil {
			t.Fatalf("temperature %v accepted", bad)
		}
	}
}

// The frame's hive temperature (#1073) decodes as a known fact and
// survives the combat read; absent it is unknown; NaN is a contract
// failure.
func TestDecodeCombatHiveTemperature(t *testing.T) {
	frame := &o.BundleSnapshot{Context: authorityTestContext(7), CombatHiveTemperatureC: proto.Float32(175)}
	combat, err := DecodeCombat(frame)
	if err != nil {
		t.Fatal(err)
	}
	if got, ok := combat.HiveTemperatureC.Value(); !ok || got != 175 {
		t.Fatalf("temperature %v %v", got, ok)
	}
	if combatFrame(frame).CombatHiveTemperatureC == nil {
		t.Fatal("the combat read drops the frame's hive temperature")
	}
	combat, err = DecodeCombat(&o.BundleSnapshot{Context: authorityTestContext(7)})
	if _, ok := combat.HiveTemperatureC.Value(); err != nil || ok {
		t.Fatal("an absent temperature is not unknown")
	}
	nan := float32(0)
	nan /= nan
	for _, bad := range []float32{nan, 5000} {
		if _, err := DecodeCombat(&o.BundleSnapshot{Context: authorityTestContext(7), CombatHiveTemperatureC: proto.Float32(bad)}); err == nil {
			t.Fatalf("temperature %v accepted", bad)
		}
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
