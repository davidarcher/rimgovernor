package policy

import (
	"math"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// testLightPrice prices a colonist at 1.6 nutrition a day against a crop whose
// normal-soil cell makes 0.055 a day, a standing lamp at 30 W and 20 steel
// (market value 38 against steel's 1.9).
func testLightPrice() LightPrice {
	return NewLightPrice(LightPriceInput{
		Model:        LightModel{LampPowerW: 30, LampBuild: 38},
		Climate:      testClimate(),
		SteelValue:   1.9,
		UpkeepPerDay: 1.6, UnitNutrition: 0.055,
	})
}

func TestLightSpeedIsStatPartGlow(t *testing.T) {
	for _, tc := range []struct{ glow, want float64 }{{0, 0.8}, {0.15, 0.9}, {0.3, 1}, {0.5, 1}, {1, 1}, {-1, 0.8}} {
		if got := LightSpeed(tc.glow); math.Abs(got-tc.want) > 1e-9 {
			t.Errorf("speed at glow %v = %v, want %v", tc.glow, got, tc.want)
		}
	}
}

func TestLightPriceCountsTheWorkDayAsFoodUpkeep(t *testing.T) {
	p := testLightPrice()
	if !p.Valid() {
		t.Fatalf("price %+v", p)
	}
	// 1.6 nutrition over 0.055 a unit is 29.09 units, at 4 units a kilowatt.
	if want := 1.6 / 0.055 / DefaultSiteTypeWeights().Power * 1000; math.Abs(p.WorkDayW-want) > 1e-6 {
		t.Errorf("work-day %v W, want %v", p.WorkDayW, want)
	}
	// The lamp is its draw plus its build cost at the layout's own rate.
	if want := 30 + BuildW(DefaultSiteTypeWeights(), 38, 1.9); math.Abs(p.LampW-want) > 1e-6 {
		t.Errorf("lamp %v W, want %v", p.LampW, want)
	}
	if got, want := p.WorkCellW(), 0.2*p.WorkDayW; math.Abs(got-want) > 1e-6 {
		t.Errorf("dark work cell %v W, want a fifth of the day %v", got, want)
	}
	if p.WorkCellW() <= p.LampW {
		t.Errorf("a dark bench (%v W) should outweigh a lamp (%v W)", p.WorkCellW(), p.LampW)
	}
	for name, in := range map[string]LightPriceInput{
		"no lamp":  {Climate: testClimate(), SteelValue: 1.9, UpkeepPerDay: 1.6, UnitNutrition: 0.055},
		"no steel": {Model: LightModel{30, 38}, Climate: testClimate(), UpkeepPerDay: 1.6, UnitNutrition: 0.055},
		"no food":  {Model: LightModel{30, 38}, Climate: testClimate(), SteelValue: 1.9, UnitNutrition: 0.055},
		"no crop":  {Model: LightModel{30, 38}, Climate: testClimate(), SteelValue: 1.9, UpkeepPerDay: 1.6},
		"no clock": {Model: LightModel{30, 38}, SteelValue: 1.9, UpkeepPerDay: 1.6, UnitNutrition: 0.055},
	} {
		if NewLightPrice(in).Valid() {
			t.Errorf("%s priced", name)
		}
	}
}

// The walked cell breaks even where the dark it costs equals a lamp: below that
// many passages a day it stays dark, above it is lit.
func TestLampPaysAboveTheWalkBreakEven(t *testing.T) {
	p := testLightPrice()
	breakEven := p.LampW / p.WalkCellW(1)
	t.Logf("a lamp pays above %.0f passages a day through one cell", breakEven)
	cell := domain.Cell{X: 3, Z: 3}
	spots := []domain.Cell{cell}
	for _, tc := range []struct {
		movements float64
		lamps     int
	}{{breakEven * 0.9, 0}, {breakEven, 0}, {breakEven * 1.1, 1}} {
		got, left := planLamps([]DarkCell{{Cell: cell, W: p.WalkCellW(tc.movements)}}, spots, p.LampW)
		if len(got) != tc.lamps {
			t.Errorf("%.0f passages: %d lamps, want %d", tc.movements, len(got), tc.lamps)
		}
		if tc.lamps == 0 && math.Abs(left-p.WalkCellW(tc.movements)) > 1e-9 {
			t.Errorf("dark left %v", left)
		}
	}
}

func TestPlanLampsShareReachAndAreDeterministic(t *testing.T) {
	p := testLightPrice()
	w := p.WorkCellW()
	near := []DarkCell{{Cell: domain.Cell{X: 0, Z: 0}, W: w}, {Cell: domain.Cell{X: 4, Z: 0}, W: w}}
	var spots []domain.Cell
	for x := int32(0); x < 5; x++ {
		spots = append(spots, domain.Cell{X: x, Z: 1})
	}
	got, left := planLamps(near, spots, p.LampW)
	if len(got) != 1 || left != 0 {
		t.Fatalf("two benches within reach: %v, left %v", got, left)
	}
	far := []DarkCell{{Cell: domain.Cell{X: 0, Z: 0}, W: w}, {Cell: domain.Cell{X: 20, Z: 0}, W: w}}
	spots = append(spots, domain.Cell{X: 20, Z: 1})
	again, _ := planLamps(far, spots, p.LampW)
	if len(again) != 2 {
		t.Fatalf("two benches out of reach: %v", again)
	}
	if other, _ := planLamps(far, spots, p.LampW); !slices.Equal(again, other) {
		t.Errorf("not deterministic: %v vs %v", again, other)
	}
}

func TestHallwayLightIsTheCheaperOfLampsAndDark(t *testing.T) {
	p := testLightPrice()
	rect := Rectangle{X: 0, Z: 0, Width: 12, Height: 1}
	quiet := map[domain.Cell]float64{{X: 5, Z: 0}: 2}
	if got, want := hallwayLightW(rect, quiet, p), p.WalkCellW(2); math.Abs(got-want) > 1e-9 {
		t.Errorf("quiet hallway %v W, want the dark %v W", got, want)
	}
	busy := map[domain.Cell]float64{{X: 5, Z: 0}: 20000}
	if got, want := hallwayLightW(rect, busy, p), p.LampW; math.Abs(got-want) > 1e-9 {
		t.Errorf("busy hallway %v W, want one lamp %v W", got, want)
	}
	if got := hallwayLightW(rect, nil, p); got != 0 {
		t.Errorf("an unwalked hallway costs %v W", got)
	}
}

// lampTileInput tiles the base with the kitchen's stove and the workshop's
// bench as worker stations a colonist stands at.
func lampTileInput(t *testing.T) TileInput {
	t.Helper()
	in, _ := tileInput(t, plainScoreSurvey(), 3, true)
	stations := map[string]Station{}
	for def, s := range in.Catalog.Stations {
		stations[def] = s
	}
	for _, def := range []string{"Stove", "Bench"} {
		s := stations[def]
		s.Capacity = StationCapacity{Kind: CapacityWorkers, Slots: 1}
		front := domain.Cell{X: 1, Z: -1}
		s.Shape.Interaction = &front
		stations[def] = s
	}
	in.Catalog.Stations = stations
	in.Catalog.Climate = testClimate()
	in.Light = testLightPrice()
	return in
}

func lampDecisions(r TileResult, reason string) []TileDecision {
	return slices.DeleteFunc(slices.Clone(r.Decisions), func(d TileDecision) bool { return d.Reason != reason })
}

func TestTilerLightsRoofedWorkRoomsOnly(t *testing.T) {
	res, err := TileEmbedding(lampTileInput(t))
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Lamps) == 0 {
		t.Fatalf("no room was lit: %+v", res.Decisions)
	}
	rooms := map[int32]PlannedRoom{}
	for _, r := range res.Plan.AllRooms() {
		rooms[r.ID] = r
	}
	for id, lamps := range res.Lamps {
		room := rooms[id]
		if !lightRoofed(room) {
			t.Errorf("room %d is under the sky and got lamps %v", id, lamps)
		}
		for _, c := range lamps {
			if !room.Region.Contains(c) {
				t.Errorf("room %d lamp %v is off the floor", id, c)
			}
			for _, piece := range res.Pieces[id] {
				if contains(piece.Rect, c) {
					t.Errorf("room %d lamp %v stands on %s", id, c, piece.Def)
				}
			}
		}
	}
	paid := lampDecisions(res, ReasonTileLamps)
	if len(paid) != len(res.Lamps) {
		t.Fatalf("%d lamp decisions for %d lit rooms", len(paid), len(res.Lamps))
	}
	for _, d := range paid {
		if d.LampW <= 0 || d.DarkW >= d.LampW {
			t.Errorf("a lit room should leave less dark than its lamps cost: %+v", d)
		}
	}
	again, err := TileEmbedding(lampTileInput(t))
	if err != nil || !slices.EqualFunc(slices.Sorted(mapKeys(res.Lamps)), slices.Sorted(mapKeys(again.Lamps)), func(a, b int32) bool { return slices.Equal(res.Lamps[a], again.Lamps[b]) }) {
		t.Errorf("lamps are not deterministic: %v vs %v (%v)", res.Lamps, again.Lamps, err)
	}
}

func mapKeys(m map[int32][]domain.Cell) func(yield func(int32) bool) {
	return func(yield func(int32) bool) {
		for k := range m {
			if !yield(k) {
				return
			}
		}
	}
}

// A cheap colonist makes the same bench not worth a lamp: the decision is the
// price, never a veto.
func TestTilerKeepsTheDarkWhenLampsCostMore(t *testing.T) {
	in := lampTileInput(t)
	in.Light.LampW = in.Light.WorkCellW() * 2
	res, err := TileEmbedding(in)
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Lamps) != 0 || len(lampDecisions(res, ReasonTileLampsNotPaid)) == 0 {
		t.Errorf("lamps %v, decisions %+v", res.Lamps, res.Decisions)
	}
}

func TestTilerSaysWhenTheLightIsUnpriced(t *testing.T) {
	in := lampTileInput(t)
	in.Light = LightPrice{}
	res, err := TileEmbedding(in)
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Lamps) != 0 || len(lampDecisions(res, ReasonTileLampsUnpriced)) == 0 {
		t.Errorf("lamps %v, decisions %+v", res.Lamps, res.Decisions)
	}
}

func TestOutdoorRoomsGetNoLamp(t *testing.T) {
	sky := PlannedRoom{Role: PlannedTrainingRange, Region: RectRegion(Rectangle{X: 0, Z: 0, Width: 8, Height: 8}), Outdoor: true}
	if lightRoofed(sky) {
		t.Error("an outdoor room is lit by the sky")
	}
}

func TestRoofingAHallwayChargesTheLampsItCauses(t *testing.T) {
	plan := roofPlan()
	plan.Trips = []RoomTrip{{A: 1, B: 2, Weight: 20000}} // the two rooms swap pawns along the hallway
	unpriced := PlanRoofed(plan, roofInput(seaIce))
	in := roofInput(seaIce)
	in.Light = testLightPrice()
	lit := PlanRoofed(plan, in)
	var hall, bare RoofDecision
	for i, d := range lit.Decisions {
		if d.Reason == ReasonRoofHallway {
			hall, bare = d, unpriced.Decisions[i]
		}
	}
	if hall.Reason != ReasonRoofHallway || hall.LightW <= 0 || hall.LightUnpriced || !bare.LightUnpriced {
		t.Fatalf("roofed hallway: %+v (unpriced %+v)", hall, bare)
	}
	if math.Abs(hall.HeldW-bare.HeldW-hall.LightW) > 1e-6 {
		t.Errorf("held %v W should be the climate's %v W plus the lamps' %v W", hall.HeldW, bare.HeldW, hall.LightW)
	}
	// Priced high enough, the dark it makes outweighs what the roof saves.
	in.Light = LightPrice{WorkDayW: 1e8, LampW: 1e8, CellWalkDays: in.Light.CellWalkDays}
	if got := PlanRoofed(plan, in); !hasRoofReason(got, ReasonRoofHallwaySkip) || got.Roofed.Contains(domain.Cell{X: 20, Z: 11}) {
		t.Errorf("a hallway whose lamps cost more than the roof saves was roofed: %+v", got.Decisions)
	}
}
