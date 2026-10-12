package policy

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func airlockTileInput(t *testing.T, curve []float64) TileInput {
	t.Helper()
	in, _ := tileInput(t, plainScoreSurvey(), 3, true)
	in.Catalog.Climate = testClimate()
	in.RoofDistance = testRoof
	in.OutdoorCurve = domain.Known(curve)
	in.SteelValue = 1.9
	in.FullRotRateC = 10
	return in
}

func airlockDecisions(r TileResult, reason string) []TileDecision {
	return slices.DeleteFunc(slices.Clone(r.Decisions), func(d TileDecision) bool { return d.Reason != reason })
}

// busy multiplies every group flow, so the trips cross the exterior doors often.
func busy(in TileInput, by float64) TileInput {
	groups := slices.Clone(in.Groups)
	for i := range groups {
		groups[i].Links = slices.Clone(groups[i].Links)
		for j := range groups[i].Links {
			groups[i].Links[j].Weight *= by
		}
	}
	in.Groups = groups
	return in
}

func TestTilerAirlocksFollowTheDoorsTraffic(t *testing.T) {
	quiet, err := TileEmbedding(airlockTileInput(t, seaIce))
	if err != nil {
		t.Fatal(err)
	}
	if len(airlockDecisions(quiet, ReasonTileAirlock)) != 0 || len(airlockDecisions(quiet, ReasonTileAirlockNotPaid)) == 0 {
		t.Errorf("a quiet base paid for airlocks: %+v", quiet.Decisions)
	}
	loud, err := TileEmbedding(busy(airlockTileInput(t, seaIce), 400))
	if err != nil {
		t.Fatal(err)
	}
	if len(airlockDecisions(loud, ReasonTileAirlock)) == 0 {
		t.Errorf("a busy base placed no airlock: %+v", loud.Decisions)
	}
}

func TestTilerAirlocksAreChildRoomsWhereTheWattsPay(t *testing.T) {
	in := busy(airlockTileInput(t, seaIce), 400)
	res, err := TileEmbedding(in)
	if err != nil {
		t.Fatal(err)
	}
	placed := airlockDecisions(res, ReasonTileAirlock)
	if len(placed) == 0 {
		t.Fatalf("no airlock on sea ice; decisions %+v", res.Decisions)
	}
	var kids int
	for _, r := range res.Plan.Rooms {
		if r.Role != PlannedAirlock {
			continue
		}
		kids++
		parent := slices.IndexFunc(res.Plan.Rooms, func(p PlannedRoom) bool { return p.ID == r.Parent })
		if r.Parent == 0 || parent < 0 || r.Vestibule() != outside(res.Plan.Rooms[parent].Door, res.Plan.Rooms[parent].DoorRot) {
			t.Errorf("airlock %d is not a child at its parent's door: %+v", r.ID, r)
		}
		if !res.Plan.Roofed.Contains(r.Vestibule()) {
			t.Errorf("airlock %d vestibule is not roofed", r.ID)
		}
	}
	if kids != len(placed) {
		t.Errorf("%d airlock rooms, %d placed decisions", kids, len(placed))
	}
	for _, d := range placed {
		if !(d.AirlockW < d.DoorW) {
			t.Errorf("airlock placed at %.2f W against a door at %.2f W", d.AirlockW, d.DoorW)
		}
	}
	if !res.Plan.Valid() {
		t.Error("the plan with airlocks is not valid")
	}
	again, _ := TileEmbedding(in)
	if !reflect.DeepEqual(res.Plan.Rooms, again.Plan.Rooms) || !reflect.DeepEqual(res.Decisions, again.Decisions) {
		t.Error("airlocks vary between runs")
	}
}

func TestTilerPlacesNoAirlockOnATemperateCurve(t *testing.T) {
	res, err := TileEmbedding(busy(airlockTileInput(t, temperate), 400))
	if err != nil {
		t.Fatal(err)
	}
	if slices.ContainsFunc(res.Plan.Rooms, func(r PlannedRoom) bool { return r.Role == PlannedAirlock }) {
		t.Error("a temperate map got an airlock")
	}
	if len(airlockDecisions(res, ReasonTileAirlockTemperate)) != 1 {
		t.Errorf("the temperate map's decision is missing: %+v", res.Decisions)
	}
}

func TestTilerAirlockNeedsAPriceNotAConstant(t *testing.T) {
	in := airlockTileInput(t, seaIce)
	in.SteelValue = 0
	res, err := TileEmbedding(in)
	if err != nil {
		t.Fatal(err)
	}
	if slices.ContainsFunc(res.Plan.Rooms, func(r PlannedRoom) bool { return r.Role == PlannedAirlock }) || len(airlockDecisions(res, ReasonTileAirlockUnpriced)) != 1 {
		t.Errorf("an unpriced airlock was placed or not reported: %+v", res.Decisions)
	}
}

func TestAirlockSavingGrowsWithTheMovements(t *testing.T) {
	// A trafficked exterior door is open more of the time, so the airlock's
	// saving over the plain door grows with the movements through it.
	low := ClimateRoom{Cells: 30, Band: ComfortBand()}
	price := func(movements float64) float64 {
		opts, err := ClimateOptions(testClimate(), DefaultSiteTypeWeights(), 1.9, seaIce, low, ClimateSide{Outdoors: true}, DoorOpenFraction(movements), []Opening{OpeningDoor, OpeningAirlock})
		if err != nil {
			t.Fatal(err)
		}
		return opts[0].TotalW - opts[1].TotalW
	}
	if price(200) <= price(2) {
		t.Errorf("airlock saving %.1f W at 200 a day is not above %.1f W at 2", price(200), price(2))
	}
}

func TestDoorOpenFractionIsBoundedAndRises(t *testing.T) {
	if DoorOpenFraction(0) != 0 || DoorOpenFraction(-3) != 0 {
		t.Error("no movements must leave the door shut")
	}
	a, b, c := DoorOpenFraction(10), DoorOpenFraction(100), DoorOpenFraction(1e9)
	if !(0 < a && a < b && b < c && c <= 1) {
		t.Errorf("fractions %v %v %v", a, b, c)
	}
}

func TestExteriorDoorMovementsChargeTheDoorsOnTheWalk(t *testing.T) {
	// Two rooms side by side, joined by a link door; trips between them cross
	// no exterior door. A trip from the first to an outdoor pen crosses the
	// first room's exterior door and the pen's gate.
	a := PlannedRoom{ID: 1, Role: PlannedKitchen, Region: RectRegion(Rectangle{X: 10, Z: 10, Width: 4, Height: 4}), Door: domain.Cell{X: 11, Z: 9}, DoorRot: domain.South}
	pen := PlannedRoom{ID: 2, Role: PlannedPen, Outdoor: true, Region: RectRegion(Rectangle{X: 20, Z: 10, Width: 4, Height: 4}), Door: domain.Cell{X: 19, Z: 11}, DoorRot: domain.West}
	p := LayoutPlan{Rooms: []PlannedRoom{a, pen}, Entrances: []domain.Cell{{X: 11, Z: 8}}, NextRoomID: 3,
		Trips: []RoomTrip{{A: 1, B: 2, Weight: 6}}}
	got := exteriorDoorMovements(p)
	if got[1] != 6 || got[2] != 6 {
		t.Errorf("movements %v, want 6 through each door", got)
	}
	p.Trips = nil
	if got := exteriorDoorMovements(p); len(got) != 0 {
		t.Errorf("a plan without trips charges %v", got)
	}
}
