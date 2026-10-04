package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

func fp(v float64) *float64 { return &v }

func sc(scalar *float64, cs ...Component) Score { return Score{Components: cs, Scalar: scalar} }

func cmp(name string, value, score *float64) Component {
	return Component{Name: name, Value: value, Score: score}
}

func TestCompareNormalDelta(t *testing.T) {
	cur := sc(fp(70), cmp("mean_mood", fp(0.7), fp(0.7)))
	base := sc(fp(60), cmp("mean_mood", fp(0.5), fp(0.5)))
	d := Compare(cur, base)
	if d.Status != deltaOK || !near(d.Scalar, 10) {
		t.Fatalf("delta %+v", d)
	}
	c := d.Components[0]
	if c.Status != deltaOK || !near(c.Delta, 0.2) || !near(c.ValueDelta, 0.2) {
		t.Errorf("component %+v", c)
	}
}

func TestCompareUnknownComponent(t *testing.T) {
	cur := sc(fp(70),
		cmp("mean_mood", fp(0.7), fp(0.7)),
		cmp("raid_damage", nil, nil),
		cmp("stage_days:Camp", fp(2), fp(0.9)))
	base := sc(nil,
		cmp("mean_mood", nil, nil),
		cmp("raid_damage", nil, nil),
		cmp("stage_days:Hut", fp(3), fp(0.8)))
	d := Compare(cur, base)
	if d.Scalar != nil {
		t.Errorf("scalar delta %v with an unknown baseline scalar", *d.Scalar)
	}
	want := map[string]bool{"mean_mood": true, "raid_damage": true, "stage_days:Camp": true, "stage_days:Hut": true}
	if len(d.Components) != len(want) {
		t.Fatalf("components %+v", d.Components)
	}
	for _, c := range d.Components {
		if !want[c.Name] || c.Status != deltaUnknown || c.Delta != nil || c.ValueDelta != nil {
			t.Errorf("%s = %+v, want unknown with no numbers", c.Name, c)
		}
	}
}

func writeRun(t *testing.T, runs, name, seed string, s Score) {
	t.Helper()
	dir := filepath.Join(runs, name)
	if err := os.MkdirAll(dir, 0755); err != nil {
		t.Fatal(err)
	}
	data, _ := json.Marshal(Summary{Meta: map[string]string{"seed": seed, "commit": name}, Score: s})
	if err := os.WriteFile(filepath.Join(dir, "run.json"), data, 0644); err != nil {
		t.Fatal(err)
	}
}

func TestCompareToStoreMissingBaseline(t *testing.T) {
	runs := t.TempDir()
	// Same seed but later than self, and an earlier run of another seed:
	// neither is this run's baseline.
	writeRun(t, runs, "2026-10-04-9", "a", sc(fp(50), cmp("mean_mood", fp(0.5), fp(0.5))))
	writeRun(t, runs, "2026-10-01-1", "b", sc(fp(50), cmp("mean_mood", fp(0.5), fp(0.5))))
	s := Summary{Meta: map[string]string{"seed": "a"}, Score: sc(fp(60), cmp("mean_mood", fp(0.6), fp(0.6)))}
	CompareToStore(&s, runs, "2026-10-03-5")
	if s.Delta == nil || s.Delta.Status != deltaNoBaseline || s.Delta.Scalar != nil || len(s.Delta.Components) != 0 {
		t.Fatalf("delta %+v", s.Delta)
	}
	var none Summary
	CompareToStore(&none, "", "x")
	if none.Delta != nil {
		t.Fatal("an empty baselines dir should skip the comparison")
	}
}

func TestCompareToStorePicksPreviousNightForSeed(t *testing.T) {
	runs := t.TempDir()
	writeRun(t, runs, "2026-10-01-1", "a", sc(fp(10), cmp("mean_mood", fp(0.1), fp(0.1))))
	writeRun(t, runs, "2026-10-02-2", "a", sc(fp(50), cmp("mean_mood", fp(0.5), fp(0.5))))
	writeRun(t, runs, "2026-10-02-3", "b", sc(fp(90), cmp("mean_mood", fp(0.9), fp(0.9))))
	s := Summary{Meta: map[string]string{"seed": "a"}, Score: sc(fp(60), cmp("mean_mood", fp(0.6), fp(0.6)))}
	CompareToStore(&s, runs, "2026-10-03-5")
	if s.Delta == nil || s.Delta.Baseline != "2026-10-02-2" || s.Delta.BaselineCommit != "2026-10-02-2" || !near(s.Delta.Scalar, 10) {
		t.Fatalf("delta %+v", s.Delta)
	}
}

func TestRunJSONCarriesDelta(t *testing.T) {
	runs := t.TempDir()
	prev := ComputeScore(fixtureRows())
	writeRun(t, runs, "2026-10-02-1", "s", prev)
	out := filepath.Join(runs, "2026-10-03-5")
	if err := Report(caseOutput(t, 10), out, map[string]string{"seed": "s"}, runs); err != nil {
		t.Fatal(err)
	}
	data, _ := os.ReadFile(filepath.Join(out, "run.json"))
	var got Summary
	if err := json.Unmarshal(data, &got); err != nil {
		t.Fatal(err)
	}
	if got.Delta == nil || got.Delta.Status != deltaOK || got.Delta.Baseline != "2026-10-02-1" || len(got.Delta.Components) == 0 {
		t.Fatalf("delta %+v", got.Delta)
	}
}
