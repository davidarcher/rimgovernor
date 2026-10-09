package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestSculptureInstall(t *testing.T) {
	ugly := RoomQuality{Wealth: 3000, Beauty: -1, Space: 25, Impressiveness: 35}
	obs, rooms, _ := upgradeFixture(t, ugly)
	targets := map[string]RoomTarget{"Room_1": {Room: "Room_1", Min: ImpressivenessSlightlyImpressive}}
	// Nothing packed: the sculpture is MaintainArt's bill, not a step here.
	if s, ok := NextSculpture(obs, targets, rooms, nil, CoreItemFacts(), RoomGate{}); ok {
		t.Fatalf("no packed = %+v", s)
	}
	if owed, _ := SculptureRoomsOwed(domain.Known(obs), targets, rooms).Value(); !owed {
		t.Fatal("beauty room not owed a sculpture")
	}
	// A packed sculpture in stock: install it on free floor.
	s, ok := NextSculpture(obs, targets, rooms, []PackedSculpture{{ID: "Thing_MinifiedSculpture1", Def: SculptureDefinition}}, CoreItemFacts(), RoomGate{})
	// Sculptures are not rotatable: the install faces North.
	if !ok || s.Room != "Room_1" || s.Packed != "Thing_MinifiedSculpture1" || s.Rot != domain.North || roomPieceOverlaps(rooms[0].Pieces, Rectangle{s.Anchor.X, s.Anchor.Z, 1, 1}) {
		t.Fatalf("install = %+v %v", s, ok)
	}
	// Beauty not the weakest stat: nothing.
	obs2, _, _ := upgradeFixture(t, RoomQuality{Wealth: 100, Beauty: 3, Space: 25, Impressiveness: 35})
	if s, ok := NextSculpture(obs2, targets, rooms, []PackedSculpture{{ID: "Thing_MinifiedSculpture1", Def: SculptureDefinition}}, CoreItemFacts(), RoomGate{}); ok {
		t.Fatalf("wealth weakest = %+v", s)
	}
	if owed, known := SculptureRoomsOwed(domain.Known(obs2), targets, rooms).Value(); !known || owed {
		t.Fatalf("wealth weakest owed = %v %v", owed, known)
	}
}

// A grand sculpture installs only on a free multi-cell spot; one
// that fits nowhere gives way to a smaller one in stock.
func TestSculptureInstallNeedsItsFootprint(t *testing.T) {
	obs, rooms, _ := upgradeFixture(t, RoomQuality{Wealth: 3000, Beauty: -1, Space: 25, Impressiveness: 35})
	targets := map[string]RoomTarget{"Room_1": {Room: "Room_1", Min: ImpressivenessSlightlyImpressive}}
	large := PackedSculpture{ID: "Thing_MinifiedSculpture2", Def: "SculptureGrand"}
	s, ok := NextSculpture(obs, targets, rooms, []PackedSculpture{large}, CoreItemFacts(), RoomGate{})
	if !ok || s.Packed != large.ID || roomPieceOverlaps(rooms[0].Pieces, OccupiedRect(s.Anchor, domain.Cell{X: 2, Z: 2}, s.Rot)) {
		t.Fatalf("large = %+v %v", s, ok)
	}
	// Block every other column: no 2x2 spot is left.
	in := rooms[0].Room.Interior
	for x := in.X + 1; x < in.X+in.Width; x += 2 {
		rooms[0].Pieces = append(rooms[0].Pieces, FurniturePiece{Def: "Wall", Rect: Rectangle{x, in.Z, 1, in.Height}})
	}
	if s, ok := NextSculpture(obs, targets, rooms, []PackedSculpture{large}, CoreItemFacts(), RoomGate{}); ok {
		t.Fatalf("large without space = %+v", s)
	}
	small := PackedSculpture{ID: "Thing_MinifiedSculpture1", Def: SculptureDefinition}
	if s, ok := NextSculpture(obs, targets, rooms, []PackedSculpture{large, small}, CoreItemFacts(), RoomGate{}); !ok || s.Packed != small.ID {
		t.Fatalf("small after large = %+v %v", s, ok)
	}
}

func TestBeautyUpgradePotThenFloor(t *testing.T) {
	ugly := RoomQuality{Wealth: 3000, Beauty: -1, Space: 25, Cleanliness: 0, Impressiveness: 35}
	obs, rooms, _ := upgradeFixture(t, ugly)
	targets := map[string]RoomTarget{"Room_1": {Room: "Room_1", Min: ImpressivenessSlightlyImpressive}}
	all := func(string) bool { return true }
	fr := FloorRoom{ID: "Room_1"}
	for x := int32(0); x < 5; x++ {
		for z := int32(0); z < 4; z++ {
			fr.Cells = append(fr.Cells, FloorCell{Cell: domain.Cell{X: x, Z: z}, Terrain: "WoodPlankFloor"})
		}
	}
	fr.Cells[0].Terrain = "Carpet"
	flooring := domain.Known(FlooringObservation{Rooms: []FloorRoom{fr}, Terrains: map[string]FloorTerrain{"WoodPlankFloor": {}, "Carpet": {Beauty: 2}}})
	floor := func(beauty float64) FloorDefinition {
		return FloorDefinition{Available: domain.Known(true), Terrain: domain.Known(true), Beauty: domain.Known(beauty)}
	}
	floors := FlooringFacts{Definitions: map[string]FloorDefinition{"WoodPlankFloor": floor(0), "Carpet": floor(2)}}

	u, ok := NextBeautyUpgrade(obs, targets, rooms, all, flooring, floors, RoomGate{})
	if !ok || u.Def != PlantPotDefinition || len(u.Cells) != 0 || roomPieceOverlaps(rooms[0].Pieces, Rectangle{u.Anchor.X, u.Anchor.Z, 1, 1}) {
		t.Fatalf("pot = %+v %v", u, ok)
	}
	rooms[0].Pieces = append(rooms[0].Pieces, FurniturePiece{Def: PlantPotDefinition, Rect: Rectangle{u.Anchor.X, u.Anchor.Z, 1, 1}})
	u, ok = NextBeautyUpgrade(obs, targets, rooms, all, flooring, floors, RoomGate{})
	if !ok || u.Def != "Carpet" || len(u.Cells) != 19 {
		t.Fatalf("floor = %+v %v", u, ok)
	}
	// Beauty not the weakest stat: nothing.
	obs2, _, _ := upgradeFixture(t, RoomQuality{Wealth: 100, Beauty: 3, Space: 25, Impressiveness: 35})
	if u, ok := NextBeautyUpgrade(obs2, targets, rooms, all, flooring, floors, RoomGate{}); ok {
		t.Fatalf("wealth weakest = %+v", u)
	}
}
