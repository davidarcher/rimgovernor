package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

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

	u, ok := NextBeautyUpgrade(obs, targets, rooms, all, flooring, floors)
	if !ok || u.Def != PlantPotDefinition || len(u.Cells) != 0 || roomPieceOverlaps(rooms[0].Pieces, Rectangle{u.Anchor.X, u.Anchor.Z, 1, 1}) {
		t.Fatalf("pot = %+v %v", u, ok)
	}
	rooms[0].Pieces = append(rooms[0].Pieces, TidyPiece{Def: PlantPotDefinition, Rect: Rectangle{u.Anchor.X, u.Anchor.Z, 1, 1}})
	u, ok = NextBeautyUpgrade(obs, targets, rooms, all, flooring, floors)
	if !ok || u.Def != "Carpet" || len(u.Cells) != 19 {
		t.Fatalf("floor = %+v %v", u, ok)
	}
	// Beauty not the weakest stat: nothing.
	obs2, _, _ := upgradeFixture(t, RoomQuality{Wealth: 100, Beauty: 3, Space: 25, Impressiveness: 35})
	if u, ok := NextBeautyUpgrade(obs2, targets, rooms, all, flooring, floors); ok {
		t.Fatalf("wealth weakest = %+v", u)
	}
}
