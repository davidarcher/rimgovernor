package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// upgradeFixture is one 5x4 bedroom at (0,0), door south of (0,0), holding
// its planned bed, with the given quality.
func upgradeFixture(t *testing.T, q RoomQuality) (SleepingObservation, []TidyRoom, InteriorPlan) {
	t.Helper()
	room := InteriorRoom{Role: RoomRoleBedroom, Interior: Rectangle{0, 0, 5, 4}, Doors: []domain.Cell{{X: 0, Z: -1}}}
	plan, ok := PlanInterior(room, InteriorPieceDef{})
	if !ok {
		t.Fatal("no bedroom plan")
	}
	tidy := TidyRoom{ID: "Room_1", Room: room}
	for _, p := range plan.Pieces {
		if p.Slot == "bed" {
			tidy.Pieces = append(tidy.Pieces, TidyPiece{Thing: "Bed_1", Def: p.Def, Size: p.Size, Rot: p.Rot, Rect: p.Rect})
		}
	}
	obs := SleepingObservation{Rooms: domain.Known([]UpkeepRoom{{ID: "Room_1", Quality: domain.Known(q)}})}
	return obs, []TidyRoom{tidy}, plan
}

func TestRoomUpgradeFillsTemplateSlotsCheapestFirst(t *testing.T) {
	low := RoomQuality{Wealth: 300, Beauty: 1, Space: 25, Cleanliness: 0, Impressiveness: 35}
	obs, rooms, plan := upgradeFixture(t, low)
	targets := map[string]RoomTarget{"Room_1": {Room: "Room_1", Min: ImpressivenessSlightlyImpressive}}
	all := func(string) bool { return true }
	u, ok := NextRoomUpgrade(obs, targets, rooms, all)
	if !ok || u.Slot != "end_table" || u.Def != "EndTable" || u.Weakest != RoomStatWealth {
		t.Fatalf("upgrade = %+v %v", u, ok)
	}
	for _, p := range plan.Pieces {
		if p.Slot == "end_table" && p.Anchor() != u.Anchor {
			t.Fatalf("anchor %v, slot %v", u.Anchor, p.Anchor())
		}
	}
	// No end table stock: the dresser is next.
	u, ok = NextRoomUpgrade(obs, targets, rooms, func(d string) bool { return d != "EndTable" })
	if !ok || u.Def != "Dresser" {
		t.Fatalf("upgrade = %+v %v", u, ok)
	}
	// Every slot filled: nothing left.
	for _, p := range plan.Pieces {
		if p.Slot != "bed" {
			rooms[0].Pieces = append(rooms[0].Pieces, TidyPiece{Def: p.Def, Rect: p.Rect})
		}
	}
	if u, ok := NextRoomUpgrade(obs, targets, rooms, all); ok {
		t.Fatalf("full room upgrade = %+v", u)
	}
}

// layout/ring (run 36991368938, #1560): a 4x3 Masonry bedroom below its
// Dull floor, its bed off the template's centre line, owes the end table
// alone; one standing anywhere in the room settles the upgrade, so the
// expansion fixture's furnished bedrooms leave MaintainHousing's bedroom
// phase on the first review.
func TestRoomUpgradeSmallBedroomSettledByAnyEndTable(t *testing.T) {
	room := InteriorRoom{Role: RoomRoleBedroom, Interior: Rectangle{0, 0, 4, 3}, Doors: []domain.Cell{{X: 4, Z: 1}}}
	tidy := TidyRoom{ID: "Room_1", Room: room, Pieces: []TidyPiece{{Thing: "Bed_1", Def: "Bed", Size: domain.Cell{X: 1, Z: 2}, Rot: domain.North, Rect: Rectangle{0, 1, 1, 2}}}}
	obs := SleepingObservation{Rooms: domain.Known([]UpkeepRoom{{ID: "Room_1", Quality: domain.Known(RoomQuality{Wealth: 196, Beauty: -1.35, Space: 15, Cleanliness: -1, Impressiveness: -9.2})}})}
	targets := map[string]RoomTarget{"Room_1": {Room: "Room_1", Min: RoomTargetBaseline(BuildTierMasonry)}}
	all := func(string) bool { return true }
	u, ok := NextRoomUpgrade(obs, targets, []TidyRoom{tidy}, all)
	if !ok || u.Def != "EndTable" {
		t.Fatalf("bare room upgrade = %+v %v, want the end table", u, ok)
	}
	tidy.Pieces = append(tidy.Pieces, TidyPiece{Thing: "EndTable_1", Def: "EndTable", Size: domain.Cell{X: 1, Z: 1}, Rect: Rectangle{1, 2, 1, 1}})
	if u, ok := NextRoomUpgrade(obs, targets, []TidyRoom{tidy}, all); ok {
		t.Fatalf("furnished room upgrade = %+v", u)
	}
}

func TestRoomUpgradeRespectsTargetsAndSpace(t *testing.T) {
	all := func(string) bool { return true }
	cases := map[string]struct {
		q      RoomQuality
		target RoomTarget
	}{
		"met":       {RoomQuality{Wealth: 300, Beauty: 1, Space: 25, Impressiveness: 52}, RoomTarget{Min: 50}},
		"never":     {RoomQuality{Wealth: 300, Beauty: 1, Space: 25, Impressiveness: 10}, RoomTarget{Max: 40, NeverUpgrade: true}},
		"ceiling":   {RoomQuality{Wealth: 300, Beauty: 1, Space: 25, Impressiveness: 41}, RoomTarget{Min: 45, Max: 40}},
		"space":     {RoomQuality{Wealth: 3000, Beauty: 5, Space: 2, Impressiveness: 20}, RoomTarget{Min: 50}},
		"no demand": {RoomQuality{Wealth: 300, Beauty: 1, Space: 25, Impressiveness: 10}, RoomTarget{}},
	}
	for name, c := range cases {
		obs, rooms, _ := upgradeFixture(t, c.q)
		if u, ok := NextRoomUpgrade(obs, map[string]RoomTarget{"Room_1": c.target}, rooms, all); ok {
			t.Errorf("%s: upgrade = %+v", name, u)
		}
	}
}

func TestWeakestRoomStat(t *testing.T) {
	if s := WeakestRoomStat(RoomQuality{Wealth: 3000, Beauty: -1, Space: 200, Cleanliness: 0}); s != RoomStatBeauty {
		t.Fatalf("weakest = %s", s)
	}
	if s := WeakestRoomStat(RoomQuality{Wealth: 3000, Beauty: 5, Space: 200, Cleanliness: -2}); s != RoomStatCleanliness {
		t.Fatalf("weakest = %s", s)
	}
}
