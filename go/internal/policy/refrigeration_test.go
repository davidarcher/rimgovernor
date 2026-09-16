package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func warmStock(id, room string, nutrition, temperature float64) FoodStorageStock {
	return FoodStorageStock{Stock: FoodStock{ID: id, Nutrition: domain.Known(nutrition), Perishable: domain.Known(true), RotTicks: domain.Known(int64(2 * ticksPerDay)),
		Roofed: domain.Known(true), TemperatureC: domain.Known(temperature), Room: domain.Known(room)}}
}

func TestRefrigerationReviewLatchesOnWarmRoofedStock(t *testing.T) {
	p := DefaultFoodStoragePolicy()
	obs := func(stocks ...FoodStorageStock) FoodStorageObservation {
		return FoodStorageObservation{Stocks: domain.Known(stocks)}
	}
	// Enter: 8 nutrition warm in room b, 4 warm in room a, one cold stock ignored.
	r, err := ReviewRefrigeration(obs(warmStock("meat", "b", 8, 20), warmStock("veg", "a", 4, 12), warmStock("cold", "c", 50, 2)), false, p)
	if err != nil || !r.Active || len(r.Rooms) != 2 || r.Rooms[0] != "a" || r.Rooms[1] != "b" {
		t.Fatal(r, err)
	}
	if n, k := r.WarmNutrition.Value(); !k || n != 12 {
		t.Fatal(r)
	}
	// Below threshold: never enters.
	r, err = ReviewRefrigeration(obs(warmStock("meat", "b", 4, 20)), false, p)
	if err != nil || r.Active || r.Rooms != nil {
		t.Fatal(r, err)
	}
	// Active: 7 C is under ChilledMaxC but above ChilledExitC, so it holds.
	r, err = ReviewRefrigeration(obs(warmStock("meat", "b", 4, 7)), true, p)
	if err != nil || !r.Active || len(r.Rooms) != 1 {
		t.Fatal(r, err)
	}
	// Releases at ChilledExitC.
	r, err = ReviewRefrigeration(obs(warmStock("meat", "b", 40, 5)), true, p)
	if err != nil || r.Active {
		t.Fatal(r, err)
	}
	// Unroofed warm stock is MaintainFoodStorage's problem, not refrigeration's.
	unroofed := warmStock("meat", "b", 40, 30)
	unroofed.Stock.Roofed = domain.Known(false)
	r, err = ReviewRefrigeration(obs(unroofed), false, p)
	if err != nil || r.Active {
		t.Fatal(r, err)
	}
	// Warm stock with a long rot runway is not at risk.
	long := warmStock("meat", "b", 40, 30)
	long.Stock.RotTicks = domain.Known(int64(10 * ticksPerDay))
	r, err = ReviewRefrigeration(obs(long), false, p)
	if err != nil || r.Active {
		t.Fatal(r, err)
	}
	// Unknown temperature on at-risk stock preserves the latch either way.
	unknown := warmStock("meat", "b", 40, 30)
	unknown.Stock.TemperatureC = domain.Unknown[float64]()
	for _, active := range []bool{true, false} {
		r, err = ReviewRefrigeration(obs(unknown), active, p)
		if err != nil || r.Active != active {
			t.Fatal(r, err)
		}
		if _, known := r.WarmNutrition.Value(); known {
			t.Fatal("unknown storage facts fabricated a measurement")
		}
	}
	if _, err = ReviewRefrigeration(obs(warmStock("dup", "b", 8, 20), warmStock("dup", "b", 8, 20)), false, p); err == nil {
		t.Fatal("duplicate stock accepted")
	}
	bad := p
	bad.FreezerTargetC = 20
	if _, err = ReviewRefrigeration(obs(), false, bad); err == nil {
		t.Fatal("freezer target above exit accepted")
	}
}

// coldRoom is a 3x3 enclosed room at (10..12, 10..12) with walls around it
// at 9 and 13; outside cells at 8 and 14 are outdoors and walkable.
func coldRoom(id string, temperature float64) (Room, []SiteCell) {
	room := Room{ID: id, Enclosed: domain.Known(true), Temperature: domain.Known(temperature), Contents: domain.Known([]Amount{})}
	var cells []SiteCell
	for x := int32(8); x <= 14; x++ {
		for z := int32(8); z <= 14; z++ {
			c := domain.Cell{X: x, Z: z}
			inside := x >= 10 && x <= 12 && z >= 10 && z <= 12
			wall := !inside && x >= 9 && x <= 13 && z >= 9 && z <= 13
			if inside {
				room.Cells = append(room.Cells, c)
			}
			cells = append(cells, SiteCell{Cell: c, Walkable: domain.Known(!wall), Occupied: domain.Known(wall), Roofed: domain.Known(inside || wall), Indoors: domain.Known(inside)})
		}
	}
	return room, cells
}

func TestRefrigerationMethodBuildsOnVentedWallThenServesExistingCooler(t *testing.T) {
	p := DefaultFoodStoragePolicy()
	review := RefrigerationReview{Active: true, WarmNutrition: domain.Known(20.0), Rooms: []string{"r"}}
	room, cells := coldRoom("r", 25)
	base := RefrigerationObservation{Rooms: []Room{room}, Cells: cells, CoolerAvailable: domain.Known(true)}

	got, err := SelectRefrigerationMethod(review, domain.Known(base), p, false)
	if err != nil || got.Method != RefrigerationBuild || got.Room != "r" {
		t.Fatal(got, err)
	}
	// Lowest-sorted wall cell with a straight inside->wall->outside line is
	// (9,10) facing west (front outward, cold side at (10,10) inside).
	if got.Cell != (domain.Cell{X: 9, Z: 10}) || got.Rotation != domain.West {
		t.Fatal(got)
	}
	if got.Key == "" {
		t.Fatal("proposal without method key")
	}

	// Cooler research missing.
	noResearch := base
	noResearch.CoolerAvailable = domain.Known(false)
	if got, err = SelectRefrigerationMethod(review, domain.Known(noResearch), p, false); err != nil || got.Method != RefrigerationResearchNeeded {
		t.Fatal(got, err)
	}

	// Existing cooler with cold side inside the room, warm target: set target.
	cooler := RefrigerationCooler{ID: "cooler", Position: domain.Cell{X: 9, Z: 10}, Rotation: domain.West, Token: "tok", Target: domain.Known(21.0), Connected: domain.Known(true), PowerOn: domain.Known(true)}
	served := base
	served.Coolers = []RefrigerationCooler{cooler}
	if got, err = SelectRefrigerationMethod(review, domain.Known(served), p, false); err != nil || got.Method != RefrigerationSetTarget || got.Cooler != "cooler" || got.Token != "tok" || got.TargetC != p.FreezerTargetC {
		t.Fatal(got, err)
	}
	// Target already set: wait for native cooling; a second cooler only once
	// the allowance elapsed.
	cooler.Target = domain.Known(-5.0)
	served.Coolers = []RefrigerationCooler{cooler}
	if got, err = SelectRefrigerationMethod(review, domain.Known(served), p, false); err != nil || got.Method != RefrigerationWait {
		t.Fatal(got, err)
	}
	if got, err = SelectRefrigerationMethod(review, domain.Known(served), p, true); err != nil || got.Method != RefrigerationBuild || got.Cell == cooler.Position {
		t.Fatal(got, err)
	}
	// Unpowered cooler defers to the power family.
	cooler.PowerOn = domain.Known(false)
	served.Coolers = []RefrigerationCooler{cooler}
	if got, err = SelectRefrigerationMethod(review, domain.Known(served), p, true); err != nil || got.Method != RefrigerationPowerNeeded {
		t.Fatal(got, err)
	}
	// Cooler facing into the room from the wall: cold side outside, hot side
	// inside -> it does not serve this room; a new one is proposed.
	backwards := cooler
	backwards.PowerOn, backwards.Rotation = domain.Known(true), domain.East
	served.Coolers = []RefrigerationCooler{backwards}
	if got, err = SelectRefrigerationMethod(review, domain.Known(served), p, false); err != nil || got.Method != RefrigerationBuild {
		t.Fatal(got, err)
	}
	// Cooler venting into a second enclosed room is blocked.
	cooler.PowerOn = domain.Known(true)
	cooler.HotIndoors = domain.Known(true)
	served.Coolers = []RefrigerationCooler{cooler}
	if got, err = SelectRefrigerationMethod(review, domain.Known(served), p, false); err != nil || got.Method != RefrigerationHeatRejectionBlocked {
		t.Fatal(got, err)
	}
	// Unknown power state on the serving cooler is unknown, not a build.
	cooler.HotIndoors = domain.Unknown[bool]()
	cooler.Connected = domain.Unknown[bool]()
	served.Coolers = []RefrigerationCooler{cooler}
	if got, err = SelectRefrigerationMethod(review, domain.Known(served), p, false); err != nil || got.Method != RefrigerationUnknown {
		t.Fatal(got, err)
	}
}

func TestRefrigerationMethodReportsEnclosureAndUnknowns(t *testing.T) {
	p := DefaultFoodStoragePolicy()
	review := RefrigerationReview{Active: true, WarmNutrition: domain.Known(20.0), Rooms: []string{"r"}}
	room, cells := coldRoom("r", 25)
	open := room
	open.Enclosed = domain.Known(false)
	got, err := SelectRefrigerationMethod(review, domain.Known(RefrigerationObservation{Rooms: []Room{open}, Cells: cells, CoolerAvailable: domain.Known(true)}), p, false)
	if err != nil || got.Method != RefrigerationEnclosureNeeded {
		t.Fatal(got, err)
	}
	// Room absent from the census.
	if got, err = SelectRefrigerationMethod(review, domain.Known(RefrigerationObservation{Cells: cells}), p, false); err != nil || got.Method != RefrigerationUnknown {
		t.Fatal(got, err)
	}
	if got, err = SelectRefrigerationMethod(review, domain.Unknown[RefrigerationObservation](), p, false); err != nil || got.Method != RefrigerationUnknown {
		t.Fatal(got, err)
	}
	if got, err = SelectRefrigerationMethod(RefrigerationReview{}, domain.Unknown[RefrigerationObservation](), p, false); err != nil || got.Method != RefrigerationNoMethod {
		t.Fatal(got, err)
	}
	// No site cells: no wall can be proven vented.
	if got, err = SelectRefrigerationMethod(review, domain.Known(RefrigerationObservation{Rooms: []Room{room}, CoolerAvailable: domain.Known(true)}), p, false); err != nil || got.Method != RefrigerationNoWall {
		t.Fatal(got, err)
	}
	// Enclosure known but a second room's cooler bad rotation is rejected.
	bad := RefrigerationObservation{Rooms: []Room{room}, Cells: cells, Coolers: []RefrigerationCooler{{ID: "c", Rotation: "up"}}}
	if _, err = SelectRefrigerationMethod(review, domain.Known(bad), p, false); err == nil {
		t.Fatal("invalid rotation accepted")
	}
}
