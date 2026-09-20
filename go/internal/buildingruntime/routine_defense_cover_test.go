package buildingruntime

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// Only ground raids whose trail entered the census count, at the first
// sampled cell inside it; a drop-pod lord and a raid that never came near
// leave no arrival.
func TestDefenseArrivalsUseFirstCrossingInsideTheCensus(t *testing.T) {
	region := bridge.CellRect{Min: domain.Cell{X: 100, Z: 100}, Max: domain.Cell{X: 144, Z: 144}}
	raids := []bridge.RaidTrack{
		{LordID: "lord-1", Ground: true, SpawnTick: 500, Spawn: domain.Cell{X: 0, Z: 120}, Trail: []domain.Cell{{X: 60, Z: 120}, {X: 98, Z: 121}, {X: 103, Z: 122}, {X: 110, Z: 122}}},
		{LordID: "lord-2", Ground: false, SpawnTick: 600, Spawn: domain.Cell{X: 120, Z: 120}},
		{LordID: "lord-3", Ground: true, SpawnTick: 700, Spawn: domain.Cell{X: 0, Z: 0}, Trail: []domain.Cell{{X: 10, Z: 10}}},
		{LordID: "lord-4", Ground: true, SpawnTick: 800, Spawn: domain.Cell{X: 100, Z: 130}},
	}
	got := defenseArrivals(raids, region)
	want := []policy.DefenseArrival{{ID: "lord-1", Edge: domain.Cell{X: 103, Z: 122}, Tick: 500}, {ID: "lord-4", Edge: domain.Cell{X: 100, Z: 130}, Tick: 800}}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("arrivals %+v, want %+v", got, want)
	}
}

// The census identity decides what the policy's demand becomes: plants,
// chunks and rock are ordered by their kind's designation; unidentified,
// already designated, player-owned and building cover is held and counted.
func TestDefenseCoverSelectionMapsKindsAndHolds(t *testing.T) {
	cell := func(x int32) domain.Cell { return domain.Cell{X: x, Z: 5} }
	approaches := policy.DefenseApproaches{Cover: []policy.DefenseCover{
		{Cell: cell(1), Fill: 0.25}, {Cell: cell(2), Fill: 0.5}, {Cell: cell(3), Fill: 1}, {Cell: cell(4), Fill: 1, Hold: "map_edge_rock"},
		{Cell: cell(5), Fill: 0.3}, {Cell: cell(6), Fill: 0.25}, {Cell: cell(7), Fill: 0.55}, {Cell: cell(8), Fill: 0.55},
	}}
	byCell := map[domain.Cell]bridge.DefenseCell{
		cell(1): {Cell: cell(1), Cover: &bridge.DefenseCover{ThingID: "Plant_TreeOak1", DefName: "Plant_TreeOak", Kind: o.CoverKind_COVER_KIND_PLANT, Token: "a"}},
		cell(2): {Cell: cell(2), Cover: &bridge.DefenseCover{ThingID: "ChunkSlateSolid2", DefName: "ChunkSlateSolid", Kind: o.CoverKind_COVER_KIND_CHUNK, Token: "b"}},
		cell(3): {Cell: cell(3), NaturalRock: true, Cover: &bridge.DefenseCover{ThingID: "Slate3", DefName: "Slate", Kind: o.CoverKind_COVER_KIND_MINEABLE, Token: "c"}},
		cell(4): {Cell: cell(4), NaturalRock: true, Cover: &bridge.DefenseCover{ThingID: "Slate4", DefName: "Slate", Kind: o.CoverKind_COVER_KIND_MINEABLE, Token: "d"}},
		cell(5): {Cell: cell(5)},
		cell(6): {Cell: cell(6), Cover: &bridge.DefenseCover{ThingID: "Plant_TreeOak6", DefName: "Plant_TreeOak", Kind: o.CoverKind_COVER_KIND_PLANT, Token: "f", Designated: true}},
		cell(7): {Cell: cell(7), PlayerOwned: true, Cover: &bridge.DefenseCover{ThingID: "Sandbags7", DefName: "Sandbags", Kind: o.CoverKind_COVER_KIND_BUILDING, Token: "g"}},
		cell(8): {Cell: cell(8), Cover: &bridge.DefenseCover{ThingID: "AncientWall8", DefName: "Wall", Kind: o.CoverKind_COVER_KIND_BUILDING, Token: "h"}},
	}
	clearances, held, err := defenseCoverSelection(approaches, byCell)
	if err != nil {
		t.Fatal(err)
	}
	var got [][2]string
	for _, c := range clearances {
		got = append(got, [2]string{c.Thing(), c.Designation()})
	}
	want := [][2]string{{"Plant_TreeOak1", domain.CoverClearanceCutPlant}, {"ChunkSlateSolid2", domain.CoverClearanceHaul}, {"Slate3", domain.CoverClearanceMine}}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("clearances %v, want %v", got, want)
	}
	if !reflect.DeepEqual(held, map[string]int{"map_edge_rock": 1, "unidentified": 1, "designated": 1, "structure": 2}) {
		t.Fatalf("held %v", held)
	}
}

// The record's geometry rebuilds as a layout whose tiers, lanes and firing
// cells the approaches protect from clearance.
func TestDefenseRecordLayoutKeepsGeometry(t *testing.T) {
	record := store.DefenseLayoutRecord{Chokepoint: domain.Cell{X: 10, Z: 10}, Entry: domain.Cell{X: 10, Z: 4}, Toward: domain.North, Width: 3,
		TrapLane: []domain.Cell{{X: 10, Z: 5}}, SafeLane: []domain.Cell{{X: 11, Z: 5}}, Firing: []domain.Cell{{X: 10, Z: 14}},
		Tiers: []store.DefenseTierRecord{{Name: policy.TierFunnel, Buildings: []store.DefenseBuilding{{Definition: "Wall", Cell: domain.Cell{X: 9, Z: 5}, Rotation: domain.North, Stuff: "WoodLog"}}, Reserved: []domain.Cell{{X: 12, Z: 5}}}}}
	layout, err := defenseRecordLayout(record)
	if err != nil {
		t.Fatal(err)
	}
	if layout.Entry != record.Entry || layout.Toward != domain.North || len(layout.Firing) != 1 || layout.Firing[0].Cell != record.Firing[0] || len(layout.Tiers) != 1 || len(layout.Tiers[0].Buildings) != 1 || layout.Tiers[0].Buildings[0].Cell() != (domain.Cell{X: 9, Z: 5}) || !reflect.DeepEqual(layout.Tiers[0].Reserved, record.Tiers[0].Reserved) {
		t.Fatalf("layout %+v", layout)
	}
}

func TestDefenseCoverAttemptsCountTheWindow(t *testing.T) {
	history := []domain.GoalMethod{{Method: "defense-cover-1000"}, {Method: "defense-cover-50000"}, {Method: "defense-rearm-t-60000"}, {Method: "defense-cover-x"}}
	if got := defenseCoverAttempts(history, 61500); got != 1 {
		t.Fatalf("attempts %d", got)
	}
	if got := defenseCoverAttempts(history, 60000); got != 2 {
		t.Fatalf("attempts %d", got)
	}
}
