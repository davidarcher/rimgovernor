package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// gateShares gives each owner a share whose remaining is the given amount
// (pool f-scaled so a lone member's share is spent + remaining).
func gateShares(remaining map[PawnID]float64) func(PawnID) PersonalShare {
	members := []ShareMember{}
	for id := range remaining {
		members = append(members, ShareMember{Profile: PawnProfile{ID: id}, Free: true, Spent: domain.Known(0.0)})
	}
	shares := PersonalShares(domain.Known(0.0), members)
	for id, r := range remaining {
		s := shares[id]
		s.Remaining = domain.Known(r)
		shares[id] = s
	}
	return func(p PawnID) PersonalShare {
		if s, ok := shares[p]; ok {
			return s
		}
		return UnknownPersonalShare()
	}
}

func pieceGate(stage ColonyStage, remaining map[PawnID]float64) RoomGate {
	prices := map[string]float64{"EndTable": 100, "Dresser": 200, "StandingLamp": 50, PlantPotDefinition: 30}
	return RoomGate{Stage: stage, Shares: gateShares(remaining), PiecePrice: func(def string) (float64, bool) {
		v, ok := prices[def]
		return v, ok
	}}
}

func TestRoomUpgradeStopsAtTheOwnersShare(t *testing.T) {
	low := RoomQuality{Wealth: 300, Beauty: 1, Space: 25, Cleanliness: 0, Impressiveness: 35}
	obs, rooms, _ := upgradeFixture(t, low)
	targets := map[string]RoomTarget{"Room_1": {Room: "Room_1", Owners: []PawnID{"a"}, Min: ImpressivenessSlightlyImpressive}}
	all := func(string) bool { return true }

	// A rich share climbs the ladder: the cheapest lever first.
	u, ok := NextRoomUpgrade(obs, targets, rooms, all, pieceGate(StageReserves, map[PawnID]float64{"a": 1000}))
	if !ok || u.Def != "EndTable" {
		t.Fatalf("rich = %+v %v", u, ok)
	}
	// A share that pays for the lamp only skips the dearer pieces.
	if u, ok := NextRoomUpgrade(obs, targets, rooms, all, pieceGate(StageReserves, map[PawnID]float64{"a": 60})); !ok || u.Slot != "lamp" {
		t.Fatalf("lamp only = %+v %v", u, ok)
	}
	// A poor share stops: nothing is due, so MaintainHousing is not held open.
	if u, ok := NextRoomUpgrade(obs, targets, rooms, all, pieceGate(StageReserves, map[PawnID]float64{"a": 10})); ok {
		t.Fatalf("poor = %+v", u)
	}
	// Share-gated upgrades begin at Reserves, however rich the owner.
	if u, ok := NextRoomUpgrade(obs, targets, rooms, all, pieceGate(StageFoothold, map[PawnID]float64{"a": 1000})); ok {
		t.Fatalf("foothold = %+v", u)
	}
	// A colonist with no share (unknown) is necessities only.
	if u, ok := NextRoomUpgrade(obs, targets, rooms, all, pieceGate(StageReserves, nil)); ok {
		t.Fatalf("unknown share = %+v", u)
	}
	// The zero-value gate is ungated; so is a room nobody owns.
	if _, ok := NextRoomUpgrade(obs, targets, rooms, all, RoomGate{}); !ok {
		t.Fatal("zero gate refused")
	}
	targets["Room_1"] = RoomTarget{Room: "Room_1", Min: ImpressivenessSlightlyImpressive}
	if _, ok := NextRoomUpgrade(obs, targets, rooms, all, pieceGate(StageFoothold, nil)); !ok {
		t.Fatal("ownerless room charged")
	}
}

func TestSharedRoomSpendsTheOwnersCombinedShare(t *testing.T) {
	low := RoomQuality{Wealth: 300, Beauty: 1, Space: 25, Cleanliness: 0, Impressiveness: 35}
	obs, rooms, _ := upgradeFixture(t, low)
	targets := map[string]RoomTarget{"Room_1": {Room: "Room_1", Owners: []PawnID{"a", "b"}, Min: ImpressivenessSlightlyImpressive}}
	all := func(string) bool { return true }
	// Neither owner alone pays 100; together they do.
	if u, ok := NextRoomUpgrade(obs, targets, rooms, all, pieceGate(StageReserves, map[PawnID]float64{"a": 60, "b": 60})); !ok || u.Def != "EndTable" {
		t.Fatalf("combined = %+v %v", u, ok)
	}
	if u, ok := NextRoomUpgrade(obs, targets, rooms, all, pieceGate(StageReserves, map[PawnID]float64{"a": 60, "b": 20})); !ok || u.Slot != "lamp" {
		t.Fatalf("short = %+v %v", u, ok)
	}
	// An owner with an unknown remaining refuses the whole room.
	if u, ok := NextRoomUpgrade(obs, targets, rooms, all, pieceGate(StageReserves, map[PawnID]float64{"a": 1000})); ok {
		t.Fatalf("unknown partner = %+v", u)
	}
}

func TestRoomGateNecessityIsNeverCharged(t *testing.T) {
	g := pieceGate(StageFoothold, nil)
	if !g.Allows([]PawnID{"a"}, 0) || !g.Allows([]PawnID{"a"}, -5) {
		t.Fatal("a necessity was charged")
	}
	if g.Allows([]PawnID{"a"}, 1) {
		t.Fatal("a charged step passed at Foothold")
	}
}

func TestBedRebuildNeedsItsPriceDelta(t *testing.T) {
	low := RoomQuality{Wealth: 300, Beauty: 1, Space: 25, Cleanliness: 0, Impressiveness: 35}
	obs, rooms, _ := upgradeFixture(t, low)
	targets := map[string]RoomTarget{"Room_1": {Room: "Room_1", Owners: []PawnID{"a"}, Min: ImpressivenessSlightlyImpressive}}
	all := func(string) bool { return true }
	obs.Beds = []SleepingBed{stuffBed("Bed_1", "Poor", "WoodLog", "a")}
	price := func(def, stuff Resource) (float64, bool) { return 100, def == "Bed" && stuff == "WoodLog" }
	// Normal 100 less Poor 75: a delta of 25.
	for _, c := range []struct {
		remaining float64
		stage     ColonyStage
		due       bool
	}{{30, StageReserves, true}, {20, StageReserves, false}, {1000, StageFoothold, false}} {
		gate := pieceGate(c.stage, map[PawnID]float64{"a": c.remaining})
		gate.BedPrice = price
		_, ok := NextBedReplacement(obs, targets, rooms, all, BedMaterials{Gate: gate})
		if ok != c.due {
			t.Fatalf("remaining %v at %v: due = %v", c.remaining, c.stage, ok)
		}
	}
	// An unpriced bed is never rebuilt while gated.
	gate := pieceGate(StageReserves, map[PawnID]float64{"a": 1000})
	if _, ok := NextBedReplacement(obs, targets, rooms, all, BedMaterials{Gate: gate}); ok {
		t.Fatal("unpriced bed rebuilt")
	}
}

func TestBeautyUpgradeIsGated(t *testing.T) {
	ugly := RoomQuality{Wealth: 3000, Beauty: -1, Space: 25, Cleanliness: 0, Impressiveness: 35}
	obs, rooms, _ := upgradeFixture(t, ugly)
	targets := map[string]RoomTarget{"Room_1": {Room: "Room_1", Owners: []PawnID{"a"}, Min: ImpressivenessSlightlyImpressive}}
	all := func(string) bool { return true }
	fr := FloorRoom{ID: "Room_1"}
	for x := int32(0); x < 5; x++ {
		for z := int32(0); z < 4; z++ {
			fr.Cells = append(fr.Cells, FloorCell{Cell: domain.Cell{X: x, Z: z}, Terrain: "WoodPlankFloor"})
		}
	}
	flooring := domain.Known(FlooringObservation{Rooms: []FloorRoom{fr}, Terrains: map[string]FloorTerrain{"WoodPlankFloor": {}, "Carpet": {Beauty: 2}}})
	floor := func(beauty float64) FloorDefinition {
		return FloorDefinition{Available: domain.Known(true), Terrain: domain.Known(true), Beauty: domain.Known(beauty), Costs: domain.Known([]Amount{{Resource: "Cloth", Count: 5}})}
	}
	floors := FlooringFacts{Definitions: map[string]FloorDefinition{"WoodPlankFloor": floor(0), "Carpet": floor(2)}, Stock: domain.Known(map[Resource]int64{"Cloth": 1000})}
	rooms[0].Pieces = append(rooms[0].Pieces, TidyPiece{Def: PlantPotDefinition, Rect: Rectangle{4, 3, 1, 1}})
	gate := pieceGate(StageReserves, map[PawnID]float64{"a": 100})
	gate.Items = ItemFacts{Market: map[Resource]float64{"Cloth": 2}}
	// Ten silver a cell: the share pays for ten of the 20 cells.
	u, ok := NextBeautyUpgrade(obs, targets, rooms, all, flooring, floors, gate)
	if !ok || u.Def != "Carpet" || len(u.Cells) != 10 {
		t.Fatalf("floor = %+v %v", u, ok)
	}
	gate = pieceGate(StageReserves, map[PawnID]float64{"a": 5})
	gate.Items = ItemFacts{Market: map[Resource]float64{"Cloth": 2}}
	if u, ok := NextBeautyUpgrade(obs, targets, rooms, all, flooring, floors, gate); ok {
		t.Fatalf("poor floor = %+v", u)
	}
	// The pot is a piece: 30 silver.
	rooms[0].Pieces = nil
	gate = pieceGate(StageReserves, map[PawnID]float64{"a": 20})
	if u, ok := NextBeautyUpgrade(obs, targets, rooms, all, flooring, floors, gate); ok && u.Def == PlantPotDefinition {
		t.Fatalf("poor pot = %+v", u)
	}
}
