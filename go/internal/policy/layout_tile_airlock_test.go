package policy

import (
	"fmt"
	"math"
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// airlockTileBase is a base of the given colonists on the curve, with the shared
// circulation entrance or a door per room, steel at its recorded value.
func airlockTileBase(t *testing.T, curve []float64, colonists int, circulation bool) TileInput {
	t.Helper()
	in, _ := tileInputCirculation(t, plainScoreSurvey(), colonists, true, circulation)
	in.Catalog.Climate = testClimate()
	in.RoofDistance = testRoof
	in.OutdoorCurve = domain.Known(curve)
	in.SteelValue = 1.9
	in.FullRotRateC = 10
	return in
}

func airlockTileInput(t *testing.T, curve []float64) TileInput {
	return airlockTileBase(t, curve, 3, true)
}

// cheapSteel makes the airlock's build a fifth of its price (steel worth 9.5
// silver a unit, not the recorded 1.9) so a fixture this tight places airlocks
// at all; the recorded price pays only at hundreds of movements a door and the
// fixtures leave no ground there.
func cheapSteel(in TileInput) TileInput {
	in.SteelValue = 9.5
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

func busiestDoor(r TileResult) float64 {
	var most float64
	for _, m := range exteriorDoorMovements(r.Plan) {
		most = max(most, m)
	}
	return most
}

// A real base of 5 or 10 colonists, on a shared entrance or a door per room,
// puts a few tens of movements a day through its busiest exterior door, a
// fraction of the sea-ice break-even (TestAirlockBreakEvenOnSeaIce): no airlock
// pays for itself and every roofed exterior door reports the totals it lost on.
func TestRealisticBasesPayForNoAirlockOnSeaIce(t *testing.T) {
	for _, colonists := range []int{5, 10} {
		for _, circulation := range []bool{true, false} {
			res, err := TileEmbedding(airlockTileBase(t, seaIce, colonists, circulation))
			if err != nil {
				t.Fatal(err)
			}
			name := fmt.Sprintf("%d colonists, circulation %v", colonists, circulation)
			if n := len(airlockDecisions(res, ReasonTileAirlock)); n != 0 {
				t.Errorf("%s: %d airlocks placed: %+v", name, n, res.Decisions)
			}
			if len(airlockDecisions(res, ReasonTileAirlockNotPaid)) == 0 {
				t.Errorf("%s: no door was priced: %+v", name, res.Decisions)
			}
			if most := busiestDoor(res); most <= 0 || most > 60 {
				t.Errorf("%s: the busiest exterior door has %.1f movements a day, want a few tens", name, most)
			}
		}
	}
}

// Past the break-even (at cheap steel) the airlocks come, and more of them as the base grows
// busier; the busy bases place them only where their decision says they pay.
func TestTilerAirlocksFollowTheDoorsTraffic(t *testing.T) {
	var placed []int
	for _, by := range []float64{1, 10, 20} {
		res, err := TileEmbedding(busy(cheapSteel(airlockTileBase(t, seaIce, 3, true)), by))
		if err != nil {
			t.Fatal(err)
		}
		placed = append(placed, len(airlockDecisions(res, ReasonTileAirlock)))
	}
	if placed[0] != 0 || placed[2] == 0 || !slices.IsSorted(placed) {
		t.Errorf("airlocks placed at 1x, 10x and 20x traffic: %v, want none, then a rising count", placed)
	}
}

func TestTilerAirlocksAreChildRoomsWhereTheWattsPay(t *testing.T) {
	in := busy(cheapSteel(airlockTileInput(t, seaIce)), 10)
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
	res, err := TileEmbedding(busy(cheapSteel(airlockTileInput(t, temperate)), 20))
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

// airlockSaving is the plain door's total watts less the airlock's for a
// comfort-band room on sea ice with the given movements a day through its door.
func airlockSaving(t *testing.T, movements float64) float64 {
	t.Helper()
	room := ClimateRoom{Cells: 30, Band: ComfortBand()}
	opts, err := ClimateOptions(testClimate(), DefaultSiteTypeWeights(), 1.9, seaIce, room, ClimateSide{Outdoors: true}, movements, []Opening{OpeningDoor, OpeningAirlock})
	if err != nil {
		t.Fatal(err)
	}
	return opts[0].TotalW - opts[1].TotalW
}

// The sea-ice break-even is the movements a day through one door at which the
// airlock's operating saving repays its build. The door's window is 181 ticks
// (45 wait, 26 walking, 110 close delay), so the door is open ~70% of the time
// at 440 a day. The audit estimated 260-280 from longer windows (a stuffed
// door opens slower and waits longer); the def's stuff-free window puts it
// near 440. Either way it is hundreds of movements a door, far above a real
// base's tens.
func TestAirlockBreakEvenOnSeaIce(t *testing.T) {
	even := 0.0
	for m := 1.0; m <= 3000; m++ {
		if airlockSaving(t, m) > 0 {
			even = m
			break
		}
	}
	if even < 250 || even > 500 {
		t.Errorf("break-even %.0f movements a door a day, want a few hundred", even)
	}
	t.Logf("sea-ice break-even %.0f movements a door a day", even)
	if airlockSaving(t, even/4) >= 0 {
		t.Errorf("the airlock pays at a quarter of the break-even")
	}
}

// A trafficked exterior door is open more of the time, so the airlock's saving
// over the plain door grows with the movements through it, up to the ~1000 a day
// where both doors stand open (a door cannot be open more than always, and the
// airlock is then 2/3 of one open door, which it saves a third of; past that the
// saving falls and the 90 W build no longer repays on sea ice).
func TestAirlockSavingGrowsWithTheMovements(t *testing.T) {
	last := airlockSaving(t, 1)
	for _, m := range []float64{10, 100, 400, 800} {
		saving := airlockSaving(t, m)
		if saving <= last {
			t.Errorf("airlock saving %.2f W at %v a day is not above %.2f W at the step before", saving, m, last)
		}
		last = saving
	}
}

// The simulated game: a one-cell vestibule between two doors that equalise in
// turn (each event moves min(rate,1)/2 of the way to its side) passes 2/3 of
// the heat one open door passes per event.
func TestAirlockOverlapPassesTwoThirdsOfOneOpenDoor(t *testing.T) {
	const outdoors, indoors, f = -30.0, 20.0, 0.5
	vestibule := indoors
	var through float64
	for i := 0; i < 200; i++ {
		vestibule += f * (outdoors - vestibule)
		moved := f * (vestibule - indoors)
		vestibule -= moved
		if i >= 100 {
			through += moved
		}
	}
	perOpenDoor := f * (outdoors - indoors) * 100
	if ratio := through / perOpenDoor; math.Abs(ratio-2.0/3) > 1e-6 {
		t.Fatalf("simulated ratio %v, want 2/3", ratio)
	}
	m := testClimate()
	// Every passage window overlaps at a flood of passages.
	if got, want := m.airlockConductance(1e9), m.DoorOpen.conductance(1)*2/3; math.Abs(got-want) > 1e-9 {
		t.Errorf("airlock under flood %v, want 2/3 of one open door %v", got, want)
	}
	if got, want := m.airlockConductance(0), series(m.DoorClosed.conductance(1), m.DoorClosed.conductance(1)); math.Abs(got-want) > 1e-12 {
		t.Errorf("idle airlock %v, want two closed doors in series %v", got, want)
	}
}

// The door window comes from the def: the wait, two cell moves and the close
// delay, and a slower door (more wait) is open more of the time.
func TestDoorWindowFollowsTheDef(t *testing.T) {
	if w := testClimate().Door.window(); w != 181 {
		t.Errorf("window %v ticks, want 45 + 26 + 110", w)
	}
	slow := testClimate()
	slow.Door.OpenTicks = 90
	if !(slow.conductance(ClimateLink{Opening: OpeningDoor}, 30, 100) > testClimate().conductance(ClimateLink{Opening: OpeningDoor}, 30, 100)) {
		t.Error("a slower door leaks no more")
	}
	if openFraction(0, 181) != 0 || openFraction(-3, 181) != 0 {
		t.Error("no movements must leave the door shut")
	}
	a, b, c := openFraction(10, 181), openFraction(100, 181), openFraction(1e9, 181)
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
	if got[1] != 12 || got[2] != 12 {
		t.Errorf("movements %v, want 12 passages (6 each way) through each door", got)
	}
	p.Trips = nil
	if got := exteriorDoorMovements(p); len(got) != 0 {
		t.Errorf("a plan without trips charges %v", got)
	}
}

func TestExteriorDoorMovementsCountTheOutsideBothWays(t *testing.T) {
	// A room's trip to the outside is walked to the nearest entrance: five
	// trips a day out are ten passages through the room's own door.
	a := PlannedRoom{ID: 1, Role: PlannedKitchen, Region: RectRegion(Rectangle{X: 10, Z: 10, Width: 4, Height: 4}), Door: domain.Cell{X: 11, Z: 9}, DoorRot: domain.South}
	p := LayoutPlan{Rooms: []PlannedRoom{a}, Entrances: []domain.Cell{{X: 11, Z: 8}}, NextRoomID: 2,
		OutsideTrips: []OutsideTrip{{Room: 1, Weight: 5}}}
	if got := exteriorDoorMovements(p); got[1] != 10 {
		t.Errorf("movements %v, want 10 passages through the door", got)
	}
	if got := tripCellMovements(p); got[a.Door] != 5 {
		t.Errorf("a cell is charged the one-way weight, got %v", got[a.Door])
	}
	p.Entrances = nil
	if got := exteriorDoorMovements(p); len(got) != 0 {
		t.Errorf("with no entrance the outside is unreachable, charged %v", got)
	}
}

// The base's colonists leave their bedrooms at wake and return to sleep: on
// sea ice the doors they share pay for airlocks that the same base without
// that traffic does not.
func TestTilerAirlockOnABusySharedEntranceFromTheDayCycle(t *testing.T) {
	in := airlockTileInput(t, seaIce)
	in.Groups = slices.Clone(in.Groups)
	for i := range in.Groups {
		if in.Groups[i].Role == RoomRoleBedroom {
			in.Groups[i].Outside = 1
		}
	}
	res, err := TileEmbedding(in)
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Plan.OutsideTrips) == 0 {
		t.Fatalf("no bedroom walks outside: %+v", res.Plan)
	}
	moves := exteriorDoorMovements(res.Plan)
	total := 0.0
	for _, m := range moves {
		total += m
	}
	if total < 2*float64(len(res.Plan.OutsideTrips)) {
		t.Errorf("%d bedroom trips out charge only %v door passages", len(res.Plan.OutsideTrips), total)
	}
	for i := range in.Groups {
		in.Groups[i].Outside *= 400
	}
	loud, err := TileEmbedding(in)
	if err != nil {
		t.Fatal(err)
	}
	if len(airlockDecisions(loud, ReasonTileAirlock)) == 0 {
		t.Errorf("a base of busy colonists placed no airlock: %+v", loud.Decisions)
	}
}
