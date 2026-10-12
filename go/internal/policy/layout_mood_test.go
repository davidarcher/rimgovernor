package policy

import (
	"math"
	"testing"
)

// testMoodBreaks is a break curve with round numbers: threshold 0.35, so the
// tiers start at 0.35 (minor), 0.2 (major) and 0.05 (extreme).
func testMoodBreaks() MoodBreakModel {
	return MoodBreakModel{Threshold: 0.35, Tiers: [moodTiers]MoodTierModel{
		TierMinor: {Weight: 3, Days: 0.6}, TierMajor: {Weight: 2, Days: 0.8}, TierExtreme: {Weight: 1, Days: 1.2},
	}}
}

func priceOf(t *testing.T, in MoodPriceInput) MoodPrice {
	t.Helper()
	if in.Model.Threshold == 0 {
		in.Model = testMoodBreaks()
	}
	if in.WorkDayW == 0 {
		in.WorkDayW = 100
	}
	p, ok := PriceMood(in)
	if !ok {
		t.Fatalf("unpriced: %+v", in)
	}
	return p
}

// TestMoodWorkedExample is the issue's worked example: the dark term (-5) takes
// a colonist at mood 0.38 to 0.33, across the minor threshold at 0.35. A minor
// break cycle is MTB 4 days, the 15000-tick cooldown (0.25 day) and the break's
// 0.6 day, of which 0.6 is lost: 0.6/4.85 work-days a day, at 100 W a work-day.
func TestMoodWorkedExample(t *testing.T) {
	p := priceOf(t, MoodPriceInput{Points: 5, Pawns: 1, ExpectedMood: 0.38})
	want := 0.6 / 4.85
	if !near(p.WorkDays, want) || !near(p.W, want*100) || p.Capped {
		t.Fatalf("price = %+v, want %v work-days", p, want)
	}
	// Across the major threshold at 0.2 the cost is the major cycle's, less what
	// the colonist already paid at minor: 0.8/(0.8+0.25+0.8) - 0.6/4.85.
	p = priceOf(t, MoodPriceInput{Points: 5, Pawns: 1, ExpectedMood: 0.23})
	if want := 0.8/1.85 - 0.6/4.85; !near(p.WorkDays, want) {
		t.Fatalf("major crossing = %v, want %v", p.WorkDays, want)
	}
}

func TestMoodPriceFollowsExpectedMood(t *testing.T) {
	high := priceOf(t, MoodPriceInput{Points: 5, Pawns: 1, ExpectedMood: 0.9})
	edge := priceOf(t, MoodPriceInput{Points: 5, Pawns: 1, ExpectedMood: 0.38})
	low := priceOf(t, MoodPriceInput{Points: 5, Pawns: 1, ExpectedMood: 0.23})
	if high.W != 0 || !(edge.W > high.W) || !(low.W > edge.W) {
		t.Fatalf("a -5 offset should cost more at low mood: high %v, edge %v, low %v", high.W, edge.W, low.W)
	}
	// Inside a tier the marginal cost is zero: a break is no likelier.
	if p := priceOf(t, MoodPriceInput{Points: 2, Pawns: 1, ExpectedMood: 0.1}); p.W != 0 {
		t.Errorf("inside the major tier a 2-point loss costs %v, want 0", p.W)
	}
	// More points cross more tiers.
	if big := priceOf(t, MoodPriceInput{Points: 20, Pawns: 1, ExpectedMood: 0.38}); !(big.W > edge.W) {
		t.Errorf("20 points (%v) should cost more than 5 (%v)", big.W, edge.W)
	}
	// Below 0 clamps to 0: an expected mood under the points still prices.
	if p := priceOf(t, MoodPriceInput{Points: 50, Pawns: 1, ExpectedMood: 0.1}); !(p.W > 0) {
		t.Errorf("clamped loss costs %v", p.W)
	}
}

func TestMoodPriceScalesWithPawns(t *testing.T) {
	one := priceOf(t, MoodPriceInput{Points: 5, Pawns: 1, ExpectedMood: 0.38})
	for _, n := range []int{0, 2, 7} {
		got := priceOf(t, MoodPriceInput{Points: 5, Pawns: n, ExpectedMood: 0.38})
		if !near(got.W, one.W*float64(n)) || !near(got.WorkDays, one.WorkDays*float64(n)) {
			t.Errorf("%d pawns = %+v, want %d x %+v", n, got, n, one)
		}
	}
}

func TestMoodPriceGainIsACredit(t *testing.T) {
	loss := priceOf(t, MoodPriceInput{Points: 5, Pawns: 2, ExpectedMood: 0.38})
	gain := priceOf(t, MoodPriceInput{Points: -5, Pawns: 2, ExpectedMood: 0.33})
	if !near(gain.W, -loss.W) {
		t.Errorf("gain %v is not the loss %v reversed", gain.W, loss.W)
	}
}

// TestMoodPriceNeverExceedsTheCheapestSource is the cross-check: mood that can
// be bought for less than the break risk is priced at the buying, and only a
// source that covers the points counts.
func TestMoodPriceNeverExceedsTheCheapestSource(t *testing.T) {
	in := MoodPriceInput{Points: 5, Pawns: 4, ExpectedMood: 0.23}
	raw := priceOf(t, in)
	in.Sources = []MoodSource{{Points: 5, W: raw.W * 2}, {Points: 3, W: 1}, {Points: 8, W: raw.W / 4}, {Points: 5, W: raw.W / 2}}
	p := priceOf(t, in)
	if !p.Capped || !near(p.W, raw.W/4) || !near(p.WorkDays, raw.WorkDays/4) {
		t.Fatalf("capped price = %+v, want %v (the 8-point source; the 3-point one buys too little)", p, raw.W/4)
	}
	in.Sources = []MoodSource{{Points: 5, W: raw.W * 3}}
	if p := priceOf(t, in); p.Capped || !near(p.W, raw.W) {
		t.Fatalf("a dearer source changed the price: %+v", p)
	}
	for _, mood := range []float64{0.9, 0.38, 0.23, 0.1} {
		in := MoodPriceInput{Points: 5, Pawns: 3, ExpectedMood: mood, Sources: []MoodSource{{Points: 6, W: 40}}}
		if p := priceOf(t, in); p.W > 40 {
			t.Errorf("mood %v: price %v exceeds the source (40 W)", mood, p.W)
		}
	}
}

func TestMoodPriceIsDeterministic(t *testing.T) {
	in := MoodPriceInput{Points: 5, Pawns: 3, ExpectedMood: 0.31, Sources: []MoodSource{{Points: 6, W: 9}, {Points: 5, W: 8}}}
	if a, b := priceOf(t, in), priceOf(t, in); a != b {
		t.Fatalf("%+v != %+v", a, b)
	}
}

func TestMoodPriceUnknownInputs(t *testing.T) {
	ok := MoodPriceInput{Model: testMoodBreaks(), WorkDayW: 100, Points: 5, Pawns: 1, ExpectedMood: 0.3}
	for name, bad := range map[string]MoodPriceInput{
		"model":      {WorkDayW: 100, Points: 5, Pawns: 1, ExpectedMood: 0.3},
		"workday":    {Model: testMoodBreaks(), Points: 5, Pawns: 1, ExpectedMood: 0.3},
		"mood":       {Model: testMoodBreaks(), WorkDayW: 100, Points: 5, Pawns: 1, ExpectedMood: 1.5},
		"nan":        {Model: testMoodBreaks(), WorkDayW: 100, Points: math.NaN(), Pawns: 1, ExpectedMood: 0.3},
		"pawns":      {Model: testMoodBreaks(), WorkDayW: 100, Points: 5, Pawns: -1, ExpectedMood: 0.3},
		"no extreme": {Model: MoodBreakModel{Threshold: 0.35, Tiers: [moodTiers]MoodTierModel{TierMinor: {Weight: 1, Days: 1}}}, WorkDayW: 100, Points: 5, Pawns: 1, ExpectedMood: 0.3},
	} {
		if _, got := PriceMood(bad); got {
			t.Errorf("%s: priced an unknown input", name)
		}
	}
	if _, got := PriceMood(ok); !got {
		t.Error("the valid input is unpriced")
	}
}

func TestStateDays(t *testing.T) {
	for _, c := range []struct {
		name               string
		min, max, mtb, day float64
	}{
		{"fixed length", 40000, 40000, 0.166, 40000.0 / 60000},
		{"no recovery roll", 10000, 20000, 0, 10000.0 / 60000},
		{"unbounded wait is min plus MTB", 10000, 99999999, 0.3, 10000.0/60000 + 0.3},
		{"truncated wait", 0, 30000, 1, 1 - math.Exp(-0.5)},
	} {
		if got := StateDays(c.min, c.max, c.mtb); !near(got, c.day) {
			t.Errorf("%s: %v, want %v", c.name, got, c.day)
		}
	}
}

func TestMoodCostPerDayIsTheBreakShareOfTheCycle(t *testing.T) {
	m := testMoodBreaks()
	for _, c := range []struct{ mood, cost float64 }{
		{0.5, 0}, {0.35, 0}, {0.34, 0.6 / 4.85}, {0.19, 0.8 / 1.85}, {0.04, 1.2 / 1.95},
	} {
		if got := m.CostPerDay(c.mood); !near(got, c.cost) {
			t.Errorf("mood %v costs %v, want %v", c.mood, got, c.cost)
		}
	}
	if m.CostPerDay(0.04) >= 1 {
		t.Error("a colonist cannot lose more than the whole day")
	}
}

func TestMoodTierAdd(t *testing.T) {
	var tier MoodTierModel
	tier = tier.Add(1, 2).Add(3, 4).Add(0, 9).Add(2, math.NaN())
	if !near(tier.Weight, 4) || !near(tier.Days, 3.5) {
		t.Fatalf("tier = %+v", tier)
	}
}

func testMoodStages() MoodModel {
	stages := func(effects ...float64) []MoodStage {
		out := make([]MoodStage, len(effects))
		for i, e := range effects {
			out[i] = MoodStage{Effect: e, Present: !math.IsNaN(e)}
			if math.IsNaN(e) {
				out[i].Effect = 0
			}
		}
		return out
	}
	nan := math.NaN()
	return MoodModel{Break: testMoodBreaks(), Stages: map[string][]MoodStage{
		"EnvironmentDark": stages(-5),
		"SleptInBedroom":  stages(-2, nan, 1, 2),
		"SleptInBarracks": stages(-7, -5, -4, -3),
	}}
}

func TestMoodLostPoints(t *testing.T) {
	m := testMoodStages()
	if dark, ok := m.DarkPoints(); !ok || dark != 5 {
		t.Errorf("dark = %v %v", dark, ok)
	}
	bedToBarracks := LostRoleEffect{Effect: EffectSleepMood, Role: RoomRoleBedroom, Got: RoomRoleBarracks}
	for stage, want := range map[int]float64{0: 5, 1: 5, 2: 5, 3: 5, 9: 5, -1: 5} {
		if got, ok := m.LostPoints(bedToBarracks, stage); !ok || got != want {
			t.Errorf("stage %d: %v %v, want %v", stage, got, ok, want)
		}
	}
	// A bedroom that is no role at all loses its whole thought; the null stage
	// gives nothing to lose.
	gone := LostRoleEffect{Effect: EffectSleepMood, Role: RoomRoleBedroom, Got: RoomRoleStoreroom}
	for stage, want := range map[int]float64{0: -2, 1: 0, 2: 1, 3: 2} {
		if got, ok := m.LostPoints(gone, stage); !ok || got != want {
			t.Errorf("bedroom lost, stage %d: %v %v, want %v", stage, got, ok, want)
		}
	}
	if got, ok := m.LostPoints(LostRoleEffect{Effect: EffectWorship, Role: RoomRoleWorshipRoom}, 2); !ok || got != 0 {
		t.Errorf("worship = %v %v", got, ok)
	}
	if _, ok := m.LostPoints(LostRoleEffect{Effect: EffectPatientMood, Role: RoomRoleHospital}, 0); ok {
		t.Error("an unknown thought priced")
	}
	if _, ok := m.LostPoints(LostRoleEffect{Effect: EffectBenchSpeed, Role: RoomRoleKitchen}, 0); ok {
		t.Error("a bench speed priced as mood")
	}
}

// TestMoodPriceOfALostBedroom runs the whole path: a barracks instead of a bedroom
// at the first stage costs 5 points to each of three sleepers.
func TestMoodPriceOfALostBedroom(t *testing.T) {
	m := testMoodStages()
	points, _ := m.LostPoints(LostRoleEffect{Effect: EffectSleepMood, Role: RoomRoleBedroom, Got: RoomRoleBarracks}, 0)
	got := priceOf(t, MoodPriceInput{Points: points, Pawns: 3, ExpectedMood: 0.38})
	if want := priceOf(t, MoodPriceInput{Points: 5, Pawns: 3, ExpectedMood: 0.38}); got != want {
		t.Fatalf("%+v != %+v", got, want)
	}
}
