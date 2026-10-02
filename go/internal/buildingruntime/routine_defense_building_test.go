package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func shooterRow(id string, x, z int32, reach float64) *o.PawnState {
	equipment := &o.PawnEquipment{Armed: proto.Bool(true), PrimaryId: proto.String(id + "-gun"), Equipped: []*o.GearItem{{Thing: &c.Ref{Id: proto.String(id + "-gun")}, Ranged: proto.Bool(true), Range: proto.Float64(reach)}}}
	return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), Position: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}}, Equipment: equipment}
}

// A ranged-equipped defender has a line of fire on a building when the
// frame's native lines see one of the building's occupied cells from the defender's cell no
// further than the weapon's range (#327); melee-armed defenders and
// out-of-range or blocked shooters are not listed.
func TestBuildingLinesOfFire(t *testing.T) {
	t.Parallel()
	ship := policy.EmergencyThreat{ID: "ship", Kind: policy.HostileBuilding, Dead: domain.Known(false), Downed: domain.Known(false), Animal: domain.Known(false), SnapshotToken: "cas", Definition: "ShipPart", Cells: []domain.Cell{{X: 20, Z: 20}, {X: 21, Z: 20}}}
	hive := policy.EmergencyThreat{ID: "hive", Kind: policy.HostileBuilding, Dead: domain.Known(false), Downed: domain.Known(false), Animal: domain.Known(false), SnapshotToken: "cas", Definition: "Hive", Cells: []domain.Cell{{X: 40, Z: 40}}}
	rows := map[string]*o.PawnState{
		"rifle":   shooterRow("rifle", 10, 20, 37),
		"bow":     shooterRow("bow", 10, 21, 8),
		"blocked": shooterRow("blocked", 30, 20, 37),
		"club":    {Pawn: &o.EntityRef{Id: proto.String("club"), Position: &c.Cell{X: proto.Int32(19), Z: proto.Int32(20)}}, Equipment: &o.PawnEquipment{Armed: proto.Bool(true), PrimaryId: proto.String("club-club"), Equipped: []*o.GearItem{{Thing: &c.Ref{Id: proto.String("club-club")}, Ranged: proto.Bool(false)}}}},
	}
	defenders := []policy.SquadDefenderFacts{
		{ID: "rifle", RangedEquipped: domain.Known(true)},
		{ID: "bow", RangedEquipped: domain.Known(true)},
		{ID: "blocked", RangedEquipped: domain.Known(true)},
		{ID: "club", RangedEquipped: domain.Known(false)},
	}
	read := []bridge.LineOfFire{
		// The rifle sees the ship's far cell only, within range.
		{From: domain.Cell{X: 10, Z: 20}, To: domain.Cell{X: 21, Z: 20}, Known: true, LineOfSight: true, Distance: 11},
		{From: domain.Cell{X: 10, Z: 20}, To: domain.Cell{X: 20, Z: 20}, Known: true, LineOfSight: false, Distance: 10},
		// The bow sees the ship but its range is 8.
		{From: domain.Cell{X: 10, Z: 21}, To: domain.Cell{X: 20, Z: 20}, Known: true, LineOfSight: true, Distance: 10.05},
		// The blocked shooter has the hive in sight, not the ship.
		{From: domain.Cell{X: 30, Z: 20}, To: domain.Cell{X: 40, Z: 40}, Known: true, LineOfSight: true, Distance: 22.4},
		{From: domain.Cell{X: 30, Z: 20}, To: domain.Cell{X: 21, Z: 20}, Known: false, Distance: 9},
	}
	lines := buildingLinesOfFire(read, []policy.EmergencyThreat{ship, hive}, defenders, rows)
	if !lines["ship"]["rifle"] || lines["ship"]["bow"] || lines["ship"]["blocked"] || lines["ship"]["club"] {
		t.Fatal(lines)
	}
	if !lines["hive"]["blocked"] || lines["hive"]["rifle"] {
		t.Fatal(lines)
	}
	// No shooter at all: no line.
	if lines = buildingLinesOfFire(read, []policy.EmergencyThreat{ship}, defenders[3:], rows); len(lines) != 0 {
		t.Fatal(lines)
	}
}
