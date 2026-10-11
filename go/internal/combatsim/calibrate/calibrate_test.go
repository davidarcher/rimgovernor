package calibrate

import (
	"errors"
	"fmt"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	"github.com/davidarcher/RimGovernor/go/internal/combatsim"
)

// sweepOf is a 20-seed sweep whose first deaths seeds each lose one colonist,
// whose first downs seeds each down one, and whose seeds all take damage.
func sweepOf(fixture string, deaths, downs int, damage float64) *combatmetrics.Sweep {
	var rows []combatmetrics.SeedRow
	for seed := 1; seed <= MinSeeds; seed++ {
		m := combatmetrics.Metrics{
			Fixture: fixture, FirstContactTick: 100, OrdersIssued: 3, ColonistDamage: damage, Winner: combatmetrics.Colonist,
			ContactToResolution: 500,
		}
		if seed <= deaths {
			m.ColonistDeaths = 1
		}
		if seed <= downs {
			m.ColonistDowns = 1
		}
		rows = append(rows, combatmetrics.SeedRow{Seed: seed, Metrics: &m})
	}
	s := combatmetrics.NewSweep(fixture, 1, "test", 0, rows)
	return &s
}

// variant pairs a real and a sim sweep, each given as (deaths, downs) seeds.
func variant(fixture, name string, real, sim [2]int) VariantInput {
	return VariantInput{Name: name, Real: sweepOf(fixture, real[0], real[1], 10), Sim: sweepOf(fixture, sim[0], sim[1], 10)}
}

// heldOut is a held-out fixture with five variants whose real deaths rise 0,
// 2, 4, 6, 8 seeds of 20 (means 0 to 0.4) and whose sim means match unless a
// test swaps them.
func heldOut(sim [5]int) FixtureInput {
	fx := FixtureInput{Name: "held", Role: HeldOut}
	for i, name := range []string{DefaultVariant, "b", "c", "d", "e"} {
		fx.Variants = append(fx.Variants, variant("held", name, [2]int{2 * i, 0}, [2]int{sim[i], 0}))
	}
	return fx
}

func calibration() FixtureInput {
	return FixtureInput{Name: "cal", Role: Calibration, Variants: []VariantInput{variant("cal", DefaultVariant, [2]int{0, 2}, [2]int{0, 2})}}
}

func hasFailure(r Report, sub string) bool {
	for _, f := range r.Failures {
		if strings.Contains(f, sub) {
			return true
		}
	}
	return false
}

func TestGatePassesWhenTheSimMatches(t *testing.T) {
	r := Evaluate(CurrentStamp(), []FixtureInput{calibration(), heldOut([5]int{0, 2, 4, 6, 8})})
	if !r.Pass || r.ExitCode() != 0 {
		t.Fatalf("matching sim failed:\n%s", r.Text())
	}
	if r.RankPairs != 10 || r.RankAgreement != 1 || len(r.Misrankings) != 0 {
		t.Errorf("pairs %d agreement %v misrankings %v", r.RankPairs, r.RankAgreement, r.Misrankings)
	}
	if len(r.Regrets) != 1 || r.Regrets[0].SimPick != DefaultVariant || r.Regrets[0].RealBest != DefaultVariant || r.Regrets[0].Deaths != 0 {
		t.Errorf("regret = %+v", r.Regrets)
	}
}

func TestRankDisagreementFails(t *testing.T) {
	// The sim ranks the variants in reverse: every real-ordered pair disagrees.
	r := Evaluate(CurrentStamp(), []FixtureInput{calibration(), heldOut([5]int{8, 6, 4, 2, 0})})
	if r.Pass || r.ExitCode() != 1 || !hasFailure(r, "rank agreement 0.00") {
		t.Fatalf("reversed ranking passed or lacks its failure:\n%s", r.Text())
	}
	if len(r.Regrets) != 1 || r.Regrets[0].SimPick != "e" || r.Regrets[0].Deaths <= 0 {
		t.Errorf("regret should name the sim's bad pick: %+v", r.Regrets)
	}
}

func TestOneSurvivalMisrankingFailsDespiteHighAgreement(t *testing.T) {
	// Swapping the worst two: agreement 9 of 10, but the sim now prefers the
	// variant whose real sweep adds colonist deaths.
	r := Evaluate(CurrentStamp(), []FixtureInput{calibration(), heldOut([5]int{0, 2, 4, 8, 6})})
	if r.RankAgreement < MinRankAgreement {
		t.Fatalf("setup: agreement %.2f is already below the bar", r.RankAgreement)
	}
	if r.Pass || len(r.Misrankings) != 1 || !hasFailure(r, "the sim prefers e over d") {
		t.Fatalf("one misranking should fail:\n%s", r.Text())
	}
}

func TestDeathsAndDownsOutOfToleranceFail(t *testing.T) {
	// The real sweep downs one colonist in each of 20 seeds: mean 1.0, so the
	// tolerance is max(25%, 1 pawn) = 1 and a sim mean in 0..2 is in.
	in := FixtureInput{Name: "cal", Role: Calibration}
	real := sweepOf("cal", 0, 20, 10)
	for _, c := range []struct {
		name          string
		deaths, downs int
	}{
		{"equal passes", 0, 20},
		{"a pawn below passes", 0, 0},
	} {
		in.Variants = []VariantInput{{Name: DefaultVariant, Real: real, Sim: sweepOf("cal", c.deaths, c.downs, 10)}}
		if r := Evaluate(CurrentStamp(), []FixtureInput{in, heldOut([5]int{0, 2, 4, 6, 8})}); !r.Pass {
			t.Errorf("%s: failed:\n%s", c.name, r.Text())
		}
	}
	// A sim whose colonists all go down twice over is out: build it by hand.
	sim := sweepOf("cal", 0, 20, 10)
	for i := range sim.Rows {
		sim.Rows[i].Metrics.ColonistDowns = 4
		sim.Rows[i].Metrics.ColonistDeaths = 3
	}
	sim.Summary = combatmetrics.Summarize(sim.Rows)
	in.Variants = []VariantInput{{Name: DefaultVariant, Real: real, Sim: sim}}
	r := Evaluate(CurrentStamp(), []FixtureInput{in, heldOut([5]int{0, 2, 4, 6, 8})})
	if r.Pass || !hasFailure(r, "colonist downs mean 4.00 vs real 1.00") || !hasFailure(r, "colonist deaths mean 3.00 vs real 0.00") {
		t.Fatalf("out-of-tolerance sim passed:\n%s", r.Text())
	}
	if len(r.Gaps) != 2 || r.Gaps[0].Excess() < r.Gaps[1].Excess() {
		t.Errorf("gaps = %+v, want both, largest first", r.Gaps)
	}
}

func TestLargeMeansUseTheRelativeTolerance(t *testing.T) {
	if got := tolerance(0.2); got != 1 {
		t.Errorf("small mean tolerance = %v, want 1 pawn", got)
	}
	if got := tolerance(8); got != 2 {
		t.Errorf("mean 8 tolerance = %v, want 25%% = 2", got)
	}
}

func TestFailsClosed(t *testing.T) {
	ok := heldOut([5]int{0, 2, 4, 6, 8})
	unsupported := FixtureInput{Name: "melee", Role: HeldOut, Unsupported: fmt.Errorf("%w: melee raiders", combatsim.ErrUnsupported)}
	noReal := heldOut([5]int{0, 2, 4, 6, 8})
	noReal.Variants[2].Real = nil
	fewSeeds := heldOut([5]int{0, 2, 4, 6, 8})
	fewSeeds.Variants[1].Real.Rows = fewSeeds.Variants[1].Real.Rows[:10]
	fewSeeds.Variants[1].Real.Summary = combatmetrics.Summarize(fewSeeds.Variants[1].Real.Rows)
	otherSeeds := heldOut([5]int{0, 2, 4, 6, 8})
	otherSeeds.Variants[1].Sim = sweepOf("held", 2, 0, 10)
	otherSeeds.Variants[1].Sim.Rows[0].Seed = 99
	simErr := heldOut([5]int{0, 2, 4, 6, 8})
	simErr.Variants[3].SimErr = errors.New("boom")
	tooFew := heldOut([5]int{0, 2, 4, 6, 8})
	tooFew.Variants = tooFew.Variants[:2]
	flat := FixtureInput{Name: "flat", Role: HeldOut}
	for _, n := range []string{DefaultVariant, "b", "c"} {
		flat.Variants = append(flat.Variants, variant("flat", n, [2]int{0, 0}, [2]int{0, 0}))
	}
	for name, c := range map[string]struct {
		in   []FixtureInput
		want string
	}{
		"unsupported fixture":       {[]FixtureInput{calibration(), ok, unsupported}, "the sim does not support this fixture"},
		"no held-out measured":      {[]FixtureInput{calibration(), unsupported}, "no held-out fixture was measured"},
		"no fixtures at all":        {nil, "no held-out fixture was measured"},
		"variant without real":      {[]FixtureInput{calibration(), noReal}, "no real sweep (unmeasured)"},
		"too few real seeds":        {[]FixtureInput{calibration(), fewSeeds}, "needs 20"},
		"seed lists differ":         {[]FixtureInput{calibration(), otherSeeds}, "differ"},
		"sim failed":                {[]FixtureInput{calibration(), simErr}, "sim could not run"},
		"too few variants":          {[]FixtureInput{calibration(), tooFew}, "a ranking needs 3"},
		"real sweeps order nothing": {[]FixtureInput{calibration(), flat}, "do not order any two variants"},
	} {
		r := Evaluate(CurrentStamp(), c.in)
		if r.Pass || r.ExitCode() != 1 || !hasFailure(r, c.want) {
			t.Errorf("%s: pass=%v, want a failure containing %q:\n%s", name, r.Pass, c.want, r.Text())
		}
	}
	// The default variant must lead.
	rev := heldOut([5]int{0, 2, 4, 6, 8})
	rev.Variants[0], rev.Variants[1] = rev.Variants[1], rev.Variants[0]
	if r := Evaluate(CurrentStamp(), []FixtureInput{calibration(), rev}); r.Pass || !hasFailure(r, "must come first") {
		t.Errorf("default not first passed:\n%s", r.Text())
	}
}

func TestFalseSafeVariantIsNamed(t *testing.T) {
	cal := FixtureInput{Name: "cal", Role: Calibration, Variants: []VariantInput{variant("cal", DefaultVariant, [2]int{1, 0}, [2]int{0, 0})}}
	r := Evaluate(CurrentStamp(), []FixtureInput{cal})
	if len(r.FalseSafe) != 1 || r.FalseSafe[0] != "cal/default" {
		t.Errorf("false safe = %v", r.FalseSafe)
	}
}

func TestChangedStampInvalidatesAPass(t *testing.T) {
	now := CurrentStamp()
	pass := Report{Stamp: now, Pass: true}
	if err := Trusted(pass, now); err != nil {
		t.Fatalf("current pass untrusted: %v", err)
	}
	for name, mutate := range map[string]func(*Stamp){
		"assembly hash":      func(s *Stamp) { s.AssemblyHash = "other" },
		"rules version":      func(s *Stamp) { s.RulesVersion++ },
		"thresholds version": func(s *Stamp) { s.ThresholdsVersion++ },
		"sweep schema":       func(s *Stamp) { s.SweepVersion++ },
	} {
		later := now
		mutate(&later)
		if err := Trusted(pass, later); !errors.Is(err, ErrStale) {
			t.Errorf("%s changed: err = %v, want ErrStale", name, err)
		}
	}
	if err := Trusted(Report{Stamp: now}, now); err == nil {
		t.Error("a failed report was trusted")
	}
}

func TestReportRoundTrips(t *testing.T) {
	r := Evaluate(CurrentStamp(), []FixtureInput{calibration(), heldOut([5]int{0, 2, 4, 8, 6})})
	data, err := r.Marshal()
	if err != nil {
		t.Fatal(err)
	}
	back, err := ParseReport(data)
	if err != nil {
		t.Fatal(err)
	}
	if back.Stamp != r.Stamp || back.Pass != r.Pass || len(back.Failures) != len(r.Failures) {
		t.Errorf("round trip changed the report")
	}
}
