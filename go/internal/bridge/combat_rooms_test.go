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

// The frame's rooms census decodes as the pods tactic's rooms:
// a proper room whose cells fill its extents, with its boundary doors and
// whether its roof is whole; an L-shaped room (its count short of its
// extents), an improper room and one past the size bound are left out.
func TestDecodeCombatRooms(t *testing.T) {
	box := func(x0, z0, x1, z1 int32) *o.Rectangle {
		return &o.Rectangle{Minimum: combatCell(x0, z0), Maximum: combatCell(x1, z1)}
	}
	frame := &o.BundleSnapshot{Context: authorityTestContext(7), Rooms: &o.RoomsSnapshot{Rooms: []*o.RoomState{
		{Id: proto.String("3"), ProperRoom: proto.Bool(true), Extents: box(45, 55, 55, 65), CellCount: proto.Uint32(121), OpenRoofCount: proto.Uint32(0), Doors: []*o.RoomDoor{{Cell: combatCell(50, 54), Outside: combatCell(50, 53)}}},
		{Id: proto.String("4"), ProperRoom: proto.Bool(true), Extents: box(10, 10, 13, 13), CellCount: proto.Uint32(12)},
		{Id: proto.String("5"), ProperRoom: proto.Bool(false), Extents: box(20, 20, 21, 21), CellCount: proto.Uint32(4)},
		{Id: proto.String("6"), ProperRoom: proto.Bool(true), Extents: box(0, 0, 32, 32), CellCount: proto.Uint32(33 * 33)},
	}}}
	combat, err := DecodeCombat(frame)
	if err != nil {
		t.Fatal(err)
	}
	want := []policy.CombatRoom{{Interior: policy.Rectangle{X: 45, Z: 55, Width: 11, Height: 11}, Doors: []domain.Cell{{X: 50, Z: 54}}, Roofed: true}}
	if !reflect.DeepEqual(combat.Rooms, want) {
		t.Fatalf("%+v", combat.Rooms)
	}
	if got := combatFrame(frame); len(got.GetRooms().GetRooms()) != 4 {
		t.Fatal("the combat read drops the frame's rooms")
	}
}

// The frame colony facts outdoor temperature decodes as a known fact and
// survives the combat read; absent it is unknown; NaN or out of range is
// a contract failure.
func TestDecodeCombatOutdoorTemperature(t *testing.T) {
	frame := &o.BundleSnapshot{Context: authorityTestContext(7), ColonyFacts: &o.ColonyFactsSnapshot{OutdoorTemperatureC: proto.Float64(-32.5)}}
	combat, err := DecodeCombat(frame)
	if err != nil {
		t.Fatal(err)
	}
	if got, ok := combat.OutdoorTemperatureC.Value(); !ok || got != -32.5 {
		t.Fatalf("temperature %v %v", got, ok)
	}
	if frameOutdoorC(combatFrame(frame)) == nil {
		t.Fatal("the combat read drops the frame's outdoor temperature")
	}
	combat, err = DecodeCombat(&o.BundleSnapshot{Context: authorityTestContext(7)})
	if _, ok := combat.OutdoorTemperatureC.Value(); err != nil || ok {
		t.Fatal("an absent temperature is not unknown")
	}
	nan := float32(0)
	nan /= nan
	for _, bad := range []float64{float64(nan), 1000} {
		if _, err := DecodeCombat(&o.BundleSnapshot{Context: authorityTestContext(7), ColonyFacts: &o.ColonyFactsSnapshot{OutdoorTemperatureC: proto.Float64(bad)}}); err == nil {
			t.Fatalf("temperature %v accepted", bad)
		}
	}
}

// The frame's hive temperature decodes as a known fact and
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
// before drop pods open; a propose still needs one.
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
