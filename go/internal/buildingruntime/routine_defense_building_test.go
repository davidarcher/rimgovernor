package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// linesNative answers one lines-of-fire read from a table keyed by the
// (from, to) pair and records the cells it was asked about.
type linesNative struct {
	RoutineDefenseSource
	firing, approach []domain.Cell
	lines            map[[2]domain.Cell]bridge.LineOfFire
	reads            int
}

func (n *linesNative) ReadLinesOfFire(ctx context.Context, _ *c.Identity, firing, approach []domain.Cell) (bridge.LinesOfFire, bridge.Result, error) {
	n.reads++
	n.firing, n.approach = firing, approach
	var out bridge.LinesOfFire
	for _, from := range firing {
		for _, to := range approach {
			line, ok := n.lines[[2]domain.Cell{from, to}]
			if !ok {
				line = bridge.LineOfFire{From: from, To: to, Known: true, LineOfSight: false, Distance: 10}
			}
			out.Lines = append(out.Lines, line)
		}
	}
	return out, bridge.Result{}, ctx.Err()
}

func shooterRow(id string, x, z int32, reach float64) *o.PawnState {
	equipment := &o.PawnEquipment{Armed: proto.Bool(true), PrimaryId: proto.String(id + "-gun"), Equipped: []*o.GearItem{{Thing: &o.EntityRef{Id: proto.String(id + "-gun")}, Ranged: proto.Bool(true), Range: proto.Float64(reach)}}}
	return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), Position: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}}, Equipment: equipment}
}

// A ranged-equipped defender has a line of fire on a building when native
// sees one of the building's occupied cells from the defender's cell no
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
		"club":    {Pawn: &o.EntityRef{Id: proto.String("club"), Position: &c.Cell{X: proto.Int32(19), Z: proto.Int32(20)}}, Equipment: &o.PawnEquipment{Armed: proto.Bool(true), PrimaryId: proto.String("club-club"), Equipped: []*o.GearItem{{Thing: &o.EntityRef{Id: proto.String("club-club")}, Ranged: proto.Bool(false)}}}},
	}
	defenders := []policy.SquadDefenderFacts{
		{ID: "rifle", RangedEquipped: domain.Known(true)},
		{ID: "bow", RangedEquipped: domain.Known(true)},
		{ID: "blocked", RangedEquipped: domain.Known(true)},
		{ID: "club", RangedEquipped: domain.Known(false)},
	}
	native := &linesNative{lines: map[[2]domain.Cell]bridge.LineOfFire{
		// The rifle sees the ship's far cell only, within range.
		{{X: 10, Z: 20}, {X: 21, Z: 20}}: {From: domain.Cell{X: 10, Z: 20}, To: domain.Cell{X: 21, Z: 20}, Known: true, LineOfSight: true, Distance: 11},
		// The bow sees the ship but its range is 8.
		{{X: 10, Z: 21}, {X: 20, Z: 20}}: {From: domain.Cell{X: 10, Z: 21}, To: domain.Cell{X: 20, Z: 20}, Known: true, LineOfSight: true, Distance: 10.05},
		// The blocked shooter has the hive in sight, not the ship.
		{{X: 30, Z: 20}, {X: 40, Z: 40}}: {From: domain.Cell{X: 30, Z: 20}, To: domain.Cell{X: 40, Z: 40}, Known: true, LineOfSight: true, Distance: 22.4},
	}}
	r := &RoutineDefensePlanner{native: native}
	lines, err := r.buildingLinesOfFire(context.Background(), &c.Identity{}, []policy.EmergencyThreat{ship, hive}, defenders, rows)
	if err != nil || native.reads != 1 {
		t.Fatal(err, native.reads)
	}
	// Only the shooters' cells are probed, against every building cell.
	if len(native.firing) != 3 || len(native.approach) != 3 {
		t.Fatal(native.firing, native.approach)
	}
	if !lines["ship"]["rifle"] || lines["ship"]["bow"] || lines["ship"]["blocked"] || lines["ship"]["club"] {
		t.Fatal(lines)
	}
	if !lines["hive"]["blocked"] || lines["hive"]["rifle"] {
		t.Fatal(lines)
	}
	// No shooter at all: no read.
	native.reads = 0
	lines, err = r.buildingLinesOfFire(context.Background(), &c.Identity{}, []policy.EmergencyThreat{ship}, defenders[3:], rows)
	if err != nil || native.reads != 0 || len(lines) != 0 {
		t.Fatal(err, native.reads, lines)
	}
}
