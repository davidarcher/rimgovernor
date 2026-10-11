package combatmetrics

import (
	"math"
	"strings"
	"testing"
)

func row(seed, deaths, downs int, damage float64, c2r int) SeedRow {
	m := New("lab-choke")
	m.ColonistDeaths, m.ColonistDowns, m.ColonistDamage, m.ContactToResolution = deaths, downs, damage, c2r
	if c2r >= 0 {
		m.Winner = Colonist
	}
	return SeedRow{Seed: seed, Metrics: &m}
}

func near(t *testing.T, name string, got, want float64) {
	t.Helper()
	if math.Abs(got-want) > 0.01 {
		t.Errorf("%s = %.4f, want %.4f", name, got, want)
	}
}

func TestStatKnownInput(t *testing.T) {
	s := NewStat([]float64{1, 2, 3, 4, 5})
	near(t, "mean", s.Mean, 3)
	near(t, "sd", s.SD, math.Sqrt(2.5))
	near(t, "half width", s.Hi-s.Mean, 2.776*math.Sqrt(2.5)/math.Sqrt(5))
	near(t, "symmetric", s.Mean-s.Lo, s.Hi-s.Mean)
}

func TestStatZeroVarianceAndSmall(t *testing.T) {
	s := NewStat([]float64{2, 2, 2, 2})
	if s.Mean != 2 || s.SD != 0 || s.Lo != 2 || s.Hi != 2 {
		t.Errorf("zero variance stat %+v", s)
	}
	if one := NewStat([]float64{7}); one.Lo != 7 || one.Hi != 7 || one.N != 1 {
		t.Errorf("one run stat %+v", one)
	}
	if none := NewStat(nil); none.N != 0 {
		t.Errorf("empty stat %+v", none)
	}
}

func TestSummarizeCountsSurvivalAndSkipsFailures(t *testing.T) {
	rows := []SeedRow{row(1, 0, 0, 10, 100), row(2, 1, 2, 30, -1), row(3, 0, 1, 20, 300), {Seed: 4, Error: "boom"}}
	sum := Summarize(rows)
	if sum.Runs != 3 || sum.SeedsWithColonistDeath != 1 || sum.SeedsWithColonistDown != 2 || sum.Unresolved != 1 {
		t.Errorf("summary counts %+v", sum)
	}
	near(t, "damage mean", sum.ColonistDamage.Mean, 20)
	if sum.ContactToResolution.N != 2 {
		t.Errorf("contact-to-resolution over resolved runs only: %+v", sum.ContactToResolution)
	}
}

func sweep(rows ...SeedRow) Sweep { return NewSweep("lab-choke", 1, "load", 0, rows) }

func TestCompareDeathGainBlocksEvenWithWideInterval(t *testing.T) {
	base := sweep(row(1, 0, 1, 10, 100), row(2, 0, 1, 10, 100), row(3, 1, 0, 10, 100))
	cand := sweep(row(1, 0, 0, 5, 90), row(2, 1, 0, 5, 90), row(3, 0, 0, 5, 90))
	c, err := Compare(base, cand)
	if err != nil {
		t.Fatal(err)
	}
	if !c.Regressed() || len(c.DeathGainSeeds) != 1 || c.DeathGainSeeds[0] != 2 {
		t.Errorf("a seed that gained a death must regress: %+v", c)
	}
}

func TestCompareDownsIntervalAboveZero(t *testing.T) {
	var b, cnd []SeedRow
	for s := 1; s <= 6; s++ {
		b = append(b, row(s, 0, 0, 10, 100))
		cnd = append(cnd, row(s, 0, 1+s%2, 10, 100)) // +1 or +2 downs on every seed
	}
	c, err := Compare(sweep(b...), sweep(cnd...))
	if err != nil {
		t.Fatal(err)
	}
	if !c.Regressed() || !strings.Contains(c.Regressions[0], "colonistDowns") {
		t.Errorf("downs delta wholly above zero must regress: %+v", c.Regressions)
	}
}

func TestCompareZeroVarianceDelta(t *testing.T) {
	base := sweep(row(1, 0, 0, 10, 100), row(2, 0, 0, 10, 100), row(3, 0, 0, 10, 100))
	same, _ := Compare(base, base)
	if same.Regressed() {
		t.Errorf("identical sweeps regressed: %+v", same.Regressions)
	}
	worse := sweep(row(1, 0, 1, 10, 100), row(2, 0, 1, 10, 100), row(3, 0, 1, 10, 100))
	c, _ := Compare(base, worse)
	if !c.Regressed() {
		t.Error("a constant +1 downs delta has a zero-width interval above zero and must regress")
	}
}

func TestCompareNoisyDownsIntervalIncludesZeroPasses(t *testing.T) {
	base := sweep(row(1, 0, 1, 10, 100), row(2, 0, 0, 10, 100), row(3, 0, 1, 10, 100), row(4, 0, 0, 10, 100))
	cand := sweep(row(1, 0, 0, 10, 100), row(2, 0, 1, 10, 100), row(3, 0, 1, 10, 100), row(4, 0, 1, 10, 100))
	c, _ := Compare(base, cand)
	if c.Regressed() {
		t.Errorf("interval includes zero, no regression expected: %+v", c.Regressions)
	}
}

func TestCompareImprovementAndPairing(t *testing.T) {
	base := sweep(row(1, 1, 2, 40, 100), row(2, 1, 2, 40, 100), row(9, 0, 0, 0, 100))
	cand := sweep(row(1, 0, 0, 5, 80), row(2, 0, 1, 5, 80), row(3, 0, 0, 0, 80))
	c, err := Compare(base, cand)
	if err != nil {
		t.Fatal(err)
	}
	if c.Regressed() || len(c.Paired) != 2 || len(c.Unpaired) != 2 {
		t.Errorf("improvement paired on shared seeds only: %+v", c)
	}
	if c.Deltas[0].Measure != "colonistDeaths" || c.Deltas[0].Stat.Mean != -1 {
		t.Errorf("deaths delta leads: %+v", c.Deltas[0])
	}
	if out := c.Report(); strings.Index(out, "colonistDeaths") > strings.Index(out, "colonistDamage") {
		t.Errorf("report must print deaths before damage:\n%s", out)
	}
}

func TestCompareRefusals(t *testing.T) {
	if _, err := Compare(sweep(row(1, 0, 0, 0, 1)), sweep(row(2, 0, 0, 0, 1))); err == nil {
		t.Error("no shared seed must be refused")
	}
	other := sweep(row(1, 0, 0, 0, 1))
	other.Fixture = "lab-open"
	if _, err := Compare(sweep(row(1, 0, 0, 0, 1)), other); err == nil {
		t.Error("different fixtures must be refused")
	}
	if _, err := ParseSweep([]byte(`{"version":99}`)); err == nil {
		t.Error("another schema version must be refused")
	}
}

func TestSweepReportLeadsWithSurvival(t *testing.T) {
	out := sweep(row(1, 1, 1, 10, 100)).Report()
	if strings.Index(out, "colonist death") > strings.Index(out, "colonist damage") {
		t.Errorf("deaths must print before damage:\n%s", out)
	}
}
