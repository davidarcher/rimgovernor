package main

import (
	"encoding/json"
	"math"
	"os"
	"path/filepath"
	"testing"
)

func f(v float64) *float64 { return &v }
func s(v string) *string   { return &v }

// fixtureRows is a recorded-style timeline: three colonists, wealth
// 1000 to 1500, mood 0.5, minimum food 4 days, tiers reached on day 0 and 3.
func fixtureRows() []Row {
	n := int64(3)
	var rows []Row
	for h := 0; h < 4; h++ {
		tier := "Tent"
		if h >= 2 {
			tier = "Wood"
		}
		rows = append(rows, Row{Tick: h * 90000, Census: Census{
			Colonists: &n, FoodRunwayDays: f(float64(8 - h*4/3)), WealthTotal: f(1000 + float64(h)*500/3),
			MoodMean: f(0.5), BuildTier: s(tier)}})
	}
	return rows
}

func comp(t *testing.T, sc Score, name string) Component {
	t.Helper()
	for _, c := range sc.Components {
		if c.Name == name {
			return c
		}
	}
	t.Fatalf("no component %q in %+v", name, sc.Components)
	return Component{}
}

func near(a *float64, want float64) bool { return a != nil && math.Abs(*a-want) < 1e-9 }

func TestScoreFull(t *testing.T) {
	sc := ComputeScore(fixtureRows())
	if c := comp(t, sc, "wealth_growth"); !near(c.Value, 0.5) || !near(c.Score, 0.5) {
		t.Errorf("wealth %+v", c)
	}
	if c := comp(t, sc, "mean_mood"); !near(c.Value, 0.5) {
		t.Errorf("mood %+v", c)
	}
	if c := comp(t, sc, "min_food_runway"); !near(c.Value, 4) {
		t.Errorf("food %+v", c)
	}
	if c := comp(t, sc, "deaths"); !near(c.Value, 0) || !near(c.Score, 1) {
		t.Errorf("deaths %+v", c)
	}
	if c := comp(t, sc, "downed_time"); !near(c.Value, 0) {
		t.Errorf("downed %+v", c)
	}
	if c := comp(t, sc, "stage_days:Wood"); !near(c.Value, 3) || !near(c.Score, 0.8) || c.Weight != weightStages/2 {
		t.Errorf("stage %+v", c)
	}
	if c := comp(t, sc, "raid_damage"); c.Value != nil || c.Score != nil {
		t.Errorf("raid damage should be unknown: %+v", c)
	}
	if sc.Scalar == nil {
		t.Fatal("no scalar")
	}
}

// A component the timeline cannot supply is unknown and leaves the scalar
// as the weighted mean of the rest, not a zero in it.
func TestScoreMissingComponentIsUnknownNotZero(t *testing.T) {
	rows := fixtureRows()
	for i := range rows {
		rows[i].Census.MoodMean = nil
	}
	sc := ComputeScore(rows)
	if c := comp(t, sc, "mean_mood"); c.Value != nil || c.Score != nil {
		t.Fatalf("mood should be unknown: %+v", c)
	}
	var sum, w float64
	for _, c := range sc.Components {
		if c.Score != nil {
			sum += c.Weight * *c.Score
			w += c.Weight
		}
	}
	if !near(sc.Scalar, 100*sum/w) {
		t.Fatalf("scalar %v, want mean over known %v", sc.Scalar, 100*sum/w)
	}
}

func TestScoreDeathAndDowned(t *testing.T) {
	rows := fixtureRows()
	two := int64(2)
	rows[2].Census.Colonists, rows[3].Census.Colonists = &two, &two
	rows[1].Census.Downed = 1
	sc := ComputeScore(rows)
	if c := comp(t, sc, "deaths"); !near(c.Value, 1) || !near(c.Score, 2.0/3) {
		t.Errorf("deaths %+v", c)
	}
	if c := comp(t, sc, "downed_time"); !near(c.Value, 0.25) {
		t.Errorf("downed %+v", c)
	}
	full := ComputeScore(fixtureRows())
	if *sc.Scalar >= *full.Scalar {
		t.Errorf("a death should lower the scalar: %v vs %v", *sc.Scalar, *full.Scalar)
	}
}

func TestScoreEmptyTimeline(t *testing.T) {
	sc := ComputeScore(nil)
	if sc.Scalar != nil {
		t.Fatalf("scalar %v with no readings", *sc.Scalar)
	}
	for _, c := range sc.Components {
		if c.Value != nil {
			t.Errorf("%s known without readings", c.Name)
		}
	}
}

func TestRunJSONCarriesScore(t *testing.T) {
	out := t.TempDir()
	if err := Report(caseOutput(t, 10), out, nil); err != nil {
		t.Fatal(err)
	}
	data, _ := os.ReadFile(filepath.Join(out, "run.json"))
	var got struct{ Score Score }
	if err := json.Unmarshal(data, &got); err != nil {
		t.Fatal(err)
	}
	if got.Score.Scalar == nil || len(got.Score.Components) < 6 {
		t.Fatalf("run.json score %+v", got.Score)
	}
}
