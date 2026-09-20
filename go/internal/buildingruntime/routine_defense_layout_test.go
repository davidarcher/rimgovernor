package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestDefenseRegionStaysInsideMapAndBound(t *testing.T) {
	t.Parallel()
	bounds := policy.Bounds{Width: 250, Height: 250}
	// No complete extent geometry: the previous Home-centred derivation.
	region, err := defenseRegion(observation.ColonyProjection{Bounds: bounds, Center: domain.Cell{X: 125, Z: 125}})
	if err != nil || region.Min != (domain.Cell{X: 103, Z: 103}) || region.Max != (domain.Cell{X: 147, Z: 147}) || region.Cells() > 2048 {
		t.Fatalf("%+v %d %v", region, region.Cells(), err)
	}
	edge, err := defenseRegion(observation.ColonyProjection{Bounds: bounds, Center: domain.Cell{X: 3, Z: 248}})
	if err != nil || edge.Min != (domain.Cell{X: 0, Z: 226}) || edge.Max != (domain.Cell{X: 25, Z: 249}) {
		t.Fatalf("%+v %v", edge, err)
	}
}

// TestDefenseRegionReadsColonyExtent: with complete extent geometry the
// census window anchors on the established footprint, still holding Home.
func TestDefenseRegionReadsColonyExtent(t *testing.T) {
	t.Parallel()
	bounds := policy.Bounds{Width: 250, Height: 250}
	wall, err := domain.NewBuilding("Wall", domain.Cell{X: 150, Z: 150}, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	facts := policy.RoutineFacts{
		CurrentConstruction: domain.Known(policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{{ID: "w", Building: wall, Cells: []domain.Cell{{X: 150, Z: 150}}}}}),
		OwnedStockpiles:     domain.Known([]policy.OwnedStockpile{}),
		HomeCoverage: domain.Known(policy.HomeCoverageObservation{Targets: []policy.HomeCoverageTarget{{
			ID: "w", Cells: []domain.Cell{{X: 150, Z: 150}}, Shape: domain.Known("shape"), Missing: domain.Known(int64(0)), Excluded: domain.Known(int64(0)),
			ExtentGeometry: domain.Known(policy.HomeExtentGeometry{}),
		}}}),
	}
	projection := observation.ColonyProjection{Bounds: bounds, Center: domain.Cell{X: 125, Z: 125}, Facts: facts}
	region, err := defenseRegion(projection)
	if err != nil {
		t.Fatal(err)
	}
	// Extent centre (150,150) is 25 cells from Home; the 22-cell window is
	// shifted back so Home stays on its border.
	if region.Min != (domain.Cell{X: 125, Z: 125}) || region.Max != (domain.Cell{X: 169, Z: 169}) || region.Cells() > 2048 {
		t.Fatalf("%+v %d", region, region.Cells())
	}
	// The same facts without complete geometry keep the previous derivation.
	facts.HomeCoverage = domain.Known(policy.HomeCoverageObservation{Targets: []policy.HomeCoverageTarget{{ID: "w", Cells: []domain.Cell{{X: 150, Z: 150}}, Shape: domain.Known("shape"), Missing: domain.Known(int64(0)), Excluded: domain.Known(int64(0))}}})
	projection.Facts = facts
	if region, err = defenseRegion(projection); err != nil || region.Min != (domain.Cell{X: 103, Z: 103}) {
		t.Fatalf("%+v %v", region, err)
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
	if _, known := facts.Position.Value(); known {
		t.Fatal("position known without a cell")
	}
	row.Pawn.Position = &c.Cell{X: proto.Int32(4), Z: proto.Int32(9)}
	if position, known := defensiveThreatFacts(row).Position.Value(); !known || position != (domain.Cell{X: 4, Z: 9}) {
		t.Fatal(position, known)
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
	if _, ok := policy.SelectDefensivePositions([]domain.Cell{{X: 1, Z: 1}}, domain.North, []policy.DefensiveThreatFacts{facts}, nil); ok {
		t.Fatal("positioned on unknown lord evidence")
	}
}

func TestDefenseTierStateLivesInRecord(t *testing.T) {
	t.Parallel()
	// Settled routine plans retire out of the goal's method list, so the
	// attempt count and built flag are kept on the stored tier record.
	record := store.DefenseLayoutRecord{Tiers: []store.DefenseTierRecord{{Name: policy.TierFiringLine}, {Name: policy.TierFunnel}}}
	tier, _, ok := record.Tier(policy.TierFunnel)
	if !ok || tier.Attempts != 0 || tier.Built {
		t.Fatal(tier)
	}
	tier.Attempts++
	record.SetTier(tier)
	tier.Built = true
	record.SetTier(store.DefenseTierRecord{Name: policy.TierFiringLine, Built: true})
	record.SetTier(store.DefenseTierRecord{Name: "unknown", Built: true})
	if record.Tiers[0].Name != policy.TierFiringLine || !record.Tiers[0].Built || record.Tiers[1].Attempts != 1 || record.Tiers[1].Built || len(record.Tiers) != 2 {
		t.Fatalf("%+v", record.Tiers)
	}
	if defenseTierMethodID(record.Tiers[1]) != "defense-funnel-1" {
		t.Fatal(defenseTierMethodID(record.Tiers[1]))
	}
}

func TestDefenseTierCensusReopensLostBuildings(t *testing.T) {
	t.Parallel()
	// A raid that breaches a wall or springs a trap (trapDestroyOnSpring)
	// leaves the tier's cell without its building: the tier drops out of
	// Built with a fresh retry budget so the planner re-admits it, while a
	// tier still standing keeps its attempt count (#72).
	wall, trap := domain.Cell{X: 1, Z: 1}, domain.Cell{X: 2, Z: 2}
	record := store.DefenseLayoutRecord{Complete: true, Tiers: []store.DefenseTierRecord{
		{Name: policy.TierFunnel, Built: true, Attempts: 1, Buildings: []store.DefenseBuilding{{Definition: "Wall", Cell: wall}}},
		{Name: policy.TierTrapCorridor, Built: true, Attempts: 2, Buildings: []store.DefenseBuilding{{Definition: "TrapSpike", Cell: trap}}},
		{Name: policy.TierChokepoint, Built: false},
	}}
	if defenseTierCensus(&record, &defenseCensus{edifice: map[domain.Cell]string{wall: "Wall", trap: "TrapSpike"}}) {
		t.Fatal("unchanged census reported a change")
	}
	if !defenseTierCensus(&record, &defenseCensus{edifice: map[domain.Cell]string{wall: "Wall"}}) {
		t.Fatal("lost trap not observed")
	}
	if !record.Tiers[0].Built || record.Tiers[0].Attempts != 1 || record.Tiers[1].Built || record.Tiers[1].Attempts != 0 || record.Tiers[2].Built || !record.Complete {
		t.Fatalf("%+v", record.Tiers)
	}
	// The repair is the tier's first re-opening: its method, and so its
	// plan id, differs from every method that built the tier the first
	// time (defense-trap_corridor-0/-1), else the repair plan collides on
	// plans.id and the window refuses no_work at every step (#331).
	if record.Tiers[1].Reopened != 1 || defenseTierMethodID(record.Tiers[1]) != "defense-trap_corridor-r1-0" {
		t.Fatalf("%+v %s", record.Tiers[1], defenseTierMethodID(record.Tiers[1]))
	}
	// A rebuilt trap is standing again; a wall replaced by another edifice
	// (a raider's own sandbag, a blueprint's parent) does not count.
	if !defenseTierCensus(&record, &defenseCensus{edifice: map[domain.Cell]string{wall: "Sandbags", trap: "TrapSpike"}}) {
		t.Fatal("rebuilt trap not observed")
	}
	if record.Tiers[0].Built || record.Tiers[0].Attempts != 0 || record.Tiers[0].Reopened != 1 || !record.Tiers[1].Built || record.Tiers[1].Reopened != 1 {
		t.Fatalf("%+v", record.Tiers)
	}
	// A second loss of the same trap is a second repair with its own ids.
	if !defenseTierCensus(&record, &defenseCensus{edifice: map[domain.Cell]string{wall: "Sandbags"}}) || record.Tiers[1].Reopened != 2 || defenseTierMethodID(record.Tiers[1]) != "defense-trap_corridor-r2-0" {
		t.Fatalf("%+v", record.Tiers)
	}
}

func TestDefenseCensusFloorStandsByTerrain(t *testing.T) {
	t.Parallel()
	// The firing line floors each shooter cell so nothing grows onto the
	// position (#224). A floor is terrain, not an edifice: the tier stands
	// by the cell's terrain, and a tree that took an unfloored cell is the
	// same lost building a breached wall is.
	cover, shooter := domain.Cell{X: 113, Z: 126}, domain.Cell{X: 113, Z: 127}
	record := store.DefenseLayoutRecord{Complete: true, Tiers: []store.DefenseTierRecord{
		{Name: policy.TierFiringLine, Built: true, Attempts: 1, Buildings: []store.DefenseBuilding{{Definition: "Barricade", Cell: cover, Rotation: domain.North, Stuff: "WoodLog"}, {Definition: "WoodPlankFloor", Cell: shooter, Rotation: domain.North}}},
	}}
	if defenseTierCensus(&record, &defenseCensus{edifice: map[domain.Cell]string{cover: "Barricade"}, terrain: map[domain.Cell]string{cover: "Soil", shooter: "WoodPlankFloor"}}) {
		t.Fatal("standing floor reported a change")
	}
	if !defenseTierCensus(&record, &defenseCensus{edifice: map[domain.Cell]string{cover: "Barricade", shooter: "WoodPlankFloor"}, terrain: map[domain.Cell]string{cover: "Soil", shooter: "Soil"}}) || record.Tiers[0].Built {
		t.Fatal("a floor never stands as an edifice")
	}
	_, buildings, _ := record.Tier(policy.TierFiringLine)
	got := defenseMissingBuildings(buildings, &defenseCensus{edifice: map[domain.Cell]string{cover: "Barricade"}, terrain: map[domain.Cell]string{shooter: "Soil"}})
	if len(got) != 1 || got[0].Cell() != shooter || got[0].Definition() != "WoodPlankFloor" {
		t.Fatalf("%+v", got)
	}
	// A floor the native preview refused is dropped from the tier alone;
	// the cover and the other positions' floors stay.
	other := domain.Cell{X: 114, Z: 127}
	kept := defenseWithoutFloors([]store.DefenseBuilding{{Definition: "Barricade", Cell: cover}, {Definition: "WoodPlankFloor", Cell: shooter}, {Definition: "WoodPlankFloor", Cell: other}, {Definition: "Barricade", Cell: shooter}}, map[domain.Cell]bool{shooter: true})
	if len(kept) != 3 || kept[0].Cell != cover || kept[1].Cell != other || kept[2].Definition != "Barricade" {
		t.Fatalf("%+v", kept)
	}
}

func TestDefenseMissingBuildingsSkipsStanding(t *testing.T) {
	t.Parallel()
	// A re-opened tier is repaired by the buildings the census lost, not
	// the whole tier: previewing a standing trap is refused as an identical
	// thing, which held the corridor's repair forever after #72's rebuild.
	trapA, fenceA, trapB := domain.Cell{X: 142, Z: 132}, domain.Cell{X: 143, Z: 132}, domain.Cell{X: 142, Z: 130}
	var buildings []domain.Building
	for _, b := range []struct {
		def  string
		cell domain.Cell
	}{{"TrapSpike", trapA}, {"Fence", fenceA}, {"TrapSpike", trapB}} {
		building, err := domain.NewBuilding(b.def, b.cell, domain.North, "WoodLog")
		if err != nil {
			t.Fatal(err)
		}
		buildings = append(buildings, building)
	}
	if got := defenseMissingBuildings(buildings, nil); len(got) != 3 {
		t.Fatalf("before any census every building is missing: %+v", got)
	}
	got := defenseMissingBuildings(buildings, &defenseCensus{edifice: map[domain.Cell]string{trapA: "TrapSpike", fenceA: "Sandbags"}})
	if len(got) != 2 || got[0].Cell() != fenceA || got[1].Cell() != trapB {
		t.Fatalf("%+v", got)
	}
	if got = defenseMissingBuildings(buildings, &defenseCensus{edifice: map[domain.Cell]string{trapA: "TrapSpike", fenceA: "Fence", trapB: "TrapSpike"}}); len(got) != 0 {
		t.Fatalf("%+v", got)
	}
}

func TestDefenseReverifyDueAfterCombatOrInterval(t *testing.T) {
	t.Parallel()
	record := store.DefenseLayoutRecord{Complete: true, VerifiedTick: 10000, VerifiedCombat: "routine-1-ActiveCombat/0"}
	if defenseReverifyDue(record, 10000+defenseReverifyTicks-1, "routine-1-ActiveCombat/0") {
		t.Fatal("re-verified before the interval with no new combat")
	}
	if !defenseReverifyDue(record, 10000+defenseReverifyTicks, "routine-1-ActiveCombat/0") {
		t.Fatal("interval elapsed without re-verification")
	}
	if !defenseReverifyDue(record, 10001, "routine-1-ActiveCombat/1") {
		t.Fatal("a new combat epoch did not trigger re-verification")
	}
	if !defenseReverifyDue(store.DefenseLayoutRecord{Complete: true}, 300000, "") {
		// A record stored before verification was recorded is checked once
		// on the next step: any live game is past the interval from tick 0.
		t.Fatal("unverified record not due")
	}
}

func TestDefenseRecordRegionCoversEveryTier(t *testing.T) {
	record := store.DefenseLayoutRecord{Chokepoint: domain.Cell{X: 142, Z: 133}, Tiers: []store.DefenseTierRecord{
		{Name: "funnel", Buildings: []store.DefenseBuilding{{Cell: domain.Cell{X: 141, Z: 128}, Definition: "Wall"}, {Cell: domain.Cell{X: 144, Z: 133}, Definition: "Wall"}}},
		{Name: "firing_line", Buildings: []store.DefenseBuilding{{Cell: domain.Cell{X: 143, Z: 125}, Definition: "Barricade"}}},
	}}
	got := defenseRecordRegion(record, policy.Bounds{Width: 250, Height: 250})
	want := bridge.CellRect{Min: domain.Cell{X: 140, Z: 124}, Max: domain.Cell{X: 145, Z: 134}}
	if got != want {
		t.Fatalf("region %+v, want %+v", got, want)
	}
	edge := defenseRecordRegion(store.DefenseLayoutRecord{Chokepoint: domain.Cell{X: 0, Z: 249}}, policy.Bounds{Width: 250, Height: 250})
	if edge != (bridge.CellRect{Min: domain.Cell{X: 0, Z: 248}, Max: domain.Cell{X: 1, Z: 249}}) {
		t.Fatalf("edge region %+v", edge)
	}
}
