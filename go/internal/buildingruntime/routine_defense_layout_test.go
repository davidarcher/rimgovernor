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

func TestDefenseRegionStaysInsideMapAndBound(t *testing.T) {
	t.Parallel()
	bounds := policy.Bounds{Width: 250, Height: 250}
	region := defenseRegion(domain.Cell{X: 125, Z: 125}, bounds)
	if region.Min != (domain.Cell{X: 103, Z: 103}) || region.Max != (domain.Cell{X: 147, Z: 147}) || region.Cells() > 2048 {
		t.Fatalf("%+v %d", region, region.Cells())
	}
	edge := defenseRegion(domain.Cell{X: 3, Z: 248}, bounds)
	if edge.Min != (domain.Cell{X: 0, Z: 226}) || edge.Max != (domain.Cell{X: 25, Z: 249}) {
		t.Fatalf("%+v", edge)
	}
}

func TestDefenseCellFactsLeaveFogUnknown(t *testing.T) {
	t.Parallel()
	fogged := defenseCellFacts(bridge.DefenseCell{Cell: domain.Cell{X: 1, Z: 2}, Fogged: true, Walkable: true})
	if _, known := fogged.Walkable.Value(); known || fogged.Cell != (domain.Cell{X: 1, Z: 2}) {
		t.Fatal("fogged cell became walkable")
	}
	seen := defenseCellFacts(bridge.DefenseCell{Cell: domain.Cell{X: 1, Z: 2}, Walkable: true, Passable: true, Door: true, PlayerOwned: true, CoverFill: 0.5, EdificeDefName: "Door"})
	if door, _ := seen.Door.Value(); !door || seen.Edifice != "Door" {
		t.Fatalf("%+v", seen)
	}
	if fill, _ := seen.CoverFill.Value(); fill != 0.5 {
		t.Fatal(fill)
	}
}

func TestDefenderRangeUsesPrimaryRangedWeaponOnly(t *testing.T) {
	t.Parallel()
	gear := func(id string, ranged bool, r float64) *o.GearItem {
		return &o.GearItem{Thing: &o.EntityRef{Id: proto.String(id)}, Weapon: proto.Bool(true), Ranged: proto.Bool(ranged), Melee: proto.Bool(!ranged), Range: proto.Float64(r)}
	}
	rows := []*o.PawnState{
		{Pawn: &o.EntityRef{Id: proto.String("a")}, Equipment: &o.PawnEquipment{Armed: proto.Bool(true), PrimaryId: proto.String("rifle"), Equipped: []*o.GearItem{gear("rifle", true, 30.9)}}},
		{Pawn: &o.EntityRef{Id: proto.String("b")}, Equipment: &o.PawnEquipment{Armed: proto.Bool(true), PrimaryId: proto.String("pistol"), Equipped: []*o.GearItem{gear("knife", false, 1), gear("pistol", true, 25.9)}}},
		{Pawn: &o.EntityRef{Id: proto.String("c")}, Equipment: &o.PawnEquipment{Armed: proto.Bool(true), PrimaryId: proto.String("club"), Equipped: []*o.GearItem{gear("club", false, 1)}}},
		{Pawn: &o.EntityRef{Id: proto.String("d")}, Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}},
		{Pawn: &o.EntityRef{Id: proto.String("e")}},
	}
	count, shortest := defenderRange(rows)
	if r, known := shortest.Value(); count != 2 || !known || r != 25.9 {
		t.Fatal(count, shortest)
	}
	if count, shortest = defenderRange(rows[2:]); count != 0 {
		t.Fatal(count)
	} else if _, known := shortest.Value(); known {
		t.Fatal("range known without a ranged defender")
	}
}

func TestDefensiveThreatFactsKeepMissingLordUnknown(t *testing.T) {
	t.Parallel()
	row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("raider")}, Dead: proto.Bool(false), Downed: proto.Bool(false), Humanlike: proto.Bool(true),
		LordJobClass: proto.String("LordJob_AssaultColony"), LordToilClass: proto.String("LordToil_AssaultColony"), NearestColonistDistance: proto.Float64(40)}
	facts := defensiveThreatFacts(row)
	if job, _ := facts.LordJobClass.Value(); job != "LordJob_AssaultColony" {
		t.Fatalf("%+v", facts)
	}
	if d, known := facts.NearestColonistDistance.Value(); !known || d != 40 {
		t.Fatal(d, known)
	}
	row.LordToilClass = nil
	row.Issues = []*o.ReadIssue{{Field: proto.String("nearest_colonist_distance"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}
	facts = defensiveThreatFacts(row)
	if _, known := facts.LordToilClass.Value(); known {
		t.Fatal("missing toil became known")
	}
	if _, known := facts.NearestColonistDistance.Value(); known {
		t.Fatal("issued distance became known")
	}
	if _, ok := policy.SelectDefensivePositions([]domain.Cell{{X: 1, Z: 1}}, []policy.DefensiveThreatFacts{facts}, nil); ok {
		t.Fatal("positioned on unknown lord evidence")
	}
}
