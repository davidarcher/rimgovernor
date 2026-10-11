// Package calibrate is the sim-vs-real honesty gate for combatsim (#2844).
// It decides whether the simulator's results may be trusted for a slice by
// comparing its sweeps with real-game sweeps of the same fixtures and seeds.
//
// The thresholds are constants here, versioned with the pinned assembly hash
// and combatsim.RulesVersion in Stamp; a pass recorded under another stamp is
// stale (Trusted). The gate fails closed: a fixture the sim cannot import, a
// variant without a real sweep, a sweep with a defect or the wrong seeds, and
// a role with nothing measured are all failures, never skips.
package calibrate

import (
	"errors"
	"fmt"
	"math"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	"github.com/davidarcher/RimGovernor/go/internal/combatsim"
)

// Thresholds, fixed before any measurement (epic #2830). Changing one is a
// ThresholdsVersion bump and invalidates every recorded pass.
const (
	ThresholdsVersion = 1
	// MinRankAgreement is the pooled pairwise rank agreement of the sim's
	// ordering of variants with the real sweep's, over held-out fixtures.
	MinRankAgreement = 0.80
	// MaxMisrankings is the survival-critical misrankings allowed: zero.
	MaxMisrankings = 0
	// RelTolerance and SmallMeanPawns bound a sim mean of colonist deaths or
	// downs against the real mean: within RelTolerance of it, or within
	// SmallMeanPawns pawns when that is wider (a small mean).
	RelTolerance   = 0.25
	SmallMeanPawns = 1.0
	// MinSeeds is the seeds every sweep must have; MinVariants the variants a
	// held-out fixture must be measured under for a ranking to mean anything.
	MinSeeds    = 20
	MinVariants = 3
	// DefaultVariant is the shipped tuning, whose real sweep is the committed
	// baseline.
	DefaultVariant = "default"
)

// Role is a fixture's place in the calibration.
type Role string

const (
	// Calibration fixtures were used while building the sim: they are checked
	// against the baseline but prove nothing about ranking.
	Calibration Role = "calibration"
	// HeldOut fixtures were not: the sim's variant ranking is judged on them.
	HeldOut Role = "held-out"
)

// Stamp is what a recorded pass is valid for.
type Stamp struct {
	AssemblyHash      string `json:"assemblyHash"`
	RulesVersion      int    `json:"rulesVersion"`
	ThresholdsVersion int    `json:"thresholdsVersion"`
	SweepVersion      int    `json:"sweepVersion"`
}

// CurrentStamp is the stamp of this build.
func CurrentStamp() Stamp {
	return Stamp{
		AssemblyHash: combatsim.AssemblyCSharpSHA256, RulesVersion: combatsim.RulesVersion,
		ThresholdsVersion: ThresholdsVersion, SweepVersion: combatmetrics.SweepVersion,
	}
}

// VariantInput is one tactic variant on one fixture. Real is nil until the
// variant has been swept in the real game; SimErr is set when the sim could
// not run it.
type VariantInput struct {
	Name   string
	Real   *combatmetrics.Sweep
	Sim    *combatmetrics.Sweep
	SimErr error
}

// FixtureInput is one fixture's measurements. Unsupported is the sim's
// refusal (an ErrUnsupported chain) when it cannot represent the fixture.
type FixtureInput struct {
	Name        string
	Role        Role
	Unsupported error
	// Variants lists DefaultVariant first.
	Variants []VariantInput
}

// Gap is one sim-vs-real difference in a gated measure.
type Gap struct {
	Fixture, Variant, Measure string
	Real, Sim, Tolerance      float64
}

// Excess is how many tolerances apart the sim and real means are.
func (g Gap) Excess() float64 { return math.Abs(g.Sim-g.Real) / g.Tolerance }

// Misranking is a survival-critical misranking: the sim prefers Preferred,
// but its real sweep has more colonist deaths than Other's.
type Misranking struct {
	Fixture, Preferred, Other string
	RealDeathsPreferred       float64
	RealDeathsOther           float64
}

// Regret is the real outcome lost by following the sim's pick on a fixture.
type Regret struct {
	Fixture, SimPick, RealBest string
	Deaths, Downs, Damage      float64
}

// FixtureReport is one fixture's verdict.
type FixtureReport struct {
	Name   string `json:"name"`
	Role   Role   `json:"role"`
	Status string `json:"status"` // "measured" or why not
	// Cells are the measured variants' means, real beside sim.
	Cells []Cell `json:"cells,omitempty"`
	// Pairs and Agreed count the variant pairs the real sweep orders and the
	// ones the sim orders the same way (held-out only).
	Pairs  int `json:"pairs,omitempty"`
	Agreed int `json:"agreed,omitempty"`
}

// Cell is one variant's real and sim means.
type Cell struct {
	Variant                       string
	RealDeaths, SimDeaths         float64
	RealDowns, SimDowns           float64
	RealDamage, SimDamage         float64
	RealDeathSeeds, SimDeathSeeds int
}

// Report is the gate's output. Pass is true only when Failures is empty.
type Report struct {
	Stamp         Stamp
	Pass          bool
	Fixtures      []FixtureReport
	RankAgreement float64 // pooled over held-out fixtures; NaN-free: 0 with no pairs
	RankPairs     int
	Misrankings   []Misranking
	Regrets       []Regret
	// FalseSafe lists variants the sim predicts with no colonist death in any
	// seed where the real sweep has at least one.
	FalseSafe []string
	// Gaps lists every gated measure whose sim mean is out of tolerance, the
	// largest first.
	Gaps     []Gap
	Failures []string
}

// ErrStale is returned by Trusted for a recorded report that does not apply.
var ErrStale = errors.New("calibration report does not apply to this build")

// Trusted reports whether a recorded report licenses using the sim: it must
// have passed, and its stamp must equal the current build's.
func Trusted(rec Report, now Stamp) error {
	if rec.Stamp != now {
		return fmt.Errorf("%w: recorded under %+v, build is %+v", ErrStale, rec.Stamp, now)
	}
	if !rec.Pass {
		return errors.New("calibration report did not pass")
	}
	return nil
}

// tolerance is the allowed distance of a sim mean from the real mean.
func tolerance(real float64) float64 { return math.Max(RelTolerance*math.Abs(real), SmallMeanPawns) }

// better orders two summaries by survival first: fewer deaths, then fewer
// downs, then less colonist damage. It returns -1 when a is better, 1 when b
// is, 0 for a tie.
func better(a, b combatmetrics.Summary) int {
	for _, p := range [][2]float64{
		{a.ColonistDeaths.Mean, b.ColonistDeaths.Mean},
		{a.ColonistDowns.Mean, b.ColonistDowns.Mean},
		{a.ColonistDamage.Mean, b.ColonistDamage.Mean},
	} {
		switch {
		case p[0] < p[1]:
			return -1
		case p[0] > p[1]:
			return 1
		}
	}
	return 0
}

// seedsOf lists a sweep's seeds in order.
func seedsOf(s combatmetrics.Sweep) []int {
	var out []int
	for _, r := range s.Rows {
		out = append(out, r.Seed)
	}
	return out
}

// check refuses a pair of sweeps that cannot be compared: another fixture,
// real defects, too few seeds, or different seed lists.
func check(fixture, variant string, v VariantInput) error {
	switch {
	case v.SimErr != nil:
		return fmt.Errorf("sim could not run: %w", v.SimErr)
	case v.Real == nil:
		return errors.New("no real sweep (unmeasured)")
	case v.Sim == nil:
		return errors.New("no sim sweep")
	case v.Real.Fixture != fixture || v.Sim.Fixture != fixture:
		return fmt.Errorf("sweeps are for fixtures %q and %q", v.Real.Fixture, v.Sim.Fixture)
	}
	if d := v.Real.Defects(); len(d) > 0 {
		return fmt.Errorf("real sweep is unusable: %s", strings.Join(d, "; "))
	}
	if len(v.Real.Rows) < MinSeeds {
		return fmt.Errorf("real sweep has %d seeds, the gate needs %d", len(v.Real.Rows), MinSeeds)
	}
	if !slices.Equal(seedsOf(*v.Real), seedsOf(*v.Sim)) {
		return fmt.Errorf("real seeds %v and sim seeds %v differ", seedsOf(*v.Real), seedsOf(*v.Sim))
	}
	for _, r := range v.Sim.Rows {
		if r.Metrics == nil {
			return fmt.Errorf("sim seed %d has no metrics: %s", r.Seed, r.Error)
		}
	}
	return nil
}

// Evaluate runs the gate over the fixtures.
func Evaluate(stamp Stamp, in []FixtureInput) Report {
	rep := Report{Stamp: stamp}
	fail := func(f string, a ...any) { rep.Failures = append(rep.Failures, fmt.Sprintf(f, a...)) }
	var pairs, agreed, heldMeasured int
	for _, fx := range in {
		fr := FixtureReport{Name: fx.Name, Role: fx.Role}
		if fx.Role != Calibration && fx.Role != HeldOut {
			fail("%s: unknown role %q", fx.Name, fx.Role)
		}
		if fx.Unsupported != nil {
			fr.Status = "unsupported: " + fx.Unsupported.Error()
			fail("%s (%s): the sim does not support this fixture: %v", fx.Name, fx.Role, fx.Unsupported)
			rep.Fixtures = append(rep.Fixtures, fr)
			continue
		}
		var ok []VariantInput
		for _, v := range fx.Variants {
			if err := check(fx.Name, v.Name, v); err != nil {
				fail("%s/%s: %v", fx.Name, v.Name, err)
				continue
			}
			ok = append(ok, v)
		}
		if len(fx.Variants) == 0 || fx.Variants[0].Name != DefaultVariant {
			fail("%s: the %q variant must come first", fx.Name, DefaultVariant)
		}
		if len(ok) == 0 {
			fr.Status = "unmeasured"
			rep.Fixtures = append(rep.Fixtures, fr)
			continue
		}
		fr.Status = "measured"
		for _, v := range ok {
			rep.absolute(&fr, v)
		}
		if fx.Role == HeldOut {
			if len(ok) < MinVariants {
				fail("%s: %d variants measured, a ranking needs %d", fx.Name, len(ok), MinVariants)
			} else {
				heldMeasured++
				p, a := rep.rank(fx.Name, ok)
				fr.Pairs, fr.Agreed = p, a
				pairs, agreed = pairs+p, agreed+a
				if p == 0 {
					fail("%s: the real sweeps do not order any two variants, so the ranking is unmeasured", fx.Name)
				}
			}
		}
		rep.Fixtures = append(rep.Fixtures, fr)
	}
	if heldMeasured == 0 {
		fail("no held-out fixture was measured: rank agreement and survival misrankings are unmeasured")
	}
	rep.RankPairs = pairs
	if pairs > 0 {
		rep.RankAgreement = float64(agreed) / float64(pairs)
		if rep.RankAgreement < MinRankAgreement {
			fail("rank agreement %.2f over %d pairs is below %.2f", rep.RankAgreement, pairs, MinRankAgreement)
		}
	}
	if len(rep.Misrankings) > MaxMisrankings {
		for _, m := range rep.Misrankings {
			fail("%s: the sim prefers %s over %s but the real sweep has %.2f colonist deaths against %.2f",
				m.Fixture, m.Preferred, m.Other, m.RealDeathsPreferred, m.RealDeathsOther)
		}
	}
	slices.SortStableFunc(rep.Gaps, func(a, b Gap) int {
		switch {
		case a.Excess() > b.Excess():
			return -1
		case a.Excess() < b.Excess():
			return 1
		}
		return 0
	})
	for _, g := range rep.Gaps {
		fail("%s/%s: sim %s mean %.2f vs real %.2f, outside +/-%.2f", g.Fixture, g.Variant, g.Measure, g.Sim, g.Real, g.Tolerance)
	}
	rep.Pass = len(rep.Failures) == 0
	return rep
}

// absolute records a variant's means and the gated measures out of tolerance,
// and false-safe variants.
func (r *Report) absolute(fr *FixtureReport, v VariantInput) {
	rs, ss := v.Real.Summary, v.Sim.Summary
	fr.Cells = append(fr.Cells, Cell{
		Variant: v.Name, RealDeaths: rs.ColonistDeaths.Mean, SimDeaths: ss.ColonistDeaths.Mean,
		RealDowns: rs.ColonistDowns.Mean, SimDowns: ss.ColonistDowns.Mean,
		RealDamage: rs.ColonistDamage.Mean, SimDamage: ss.ColonistDamage.Mean,
		RealDeathSeeds: rs.SeedsWithColonistDeath, SimDeathSeeds: ss.SeedsWithColonistDeath,
	})
	for _, m := range []struct {
		name      string
		real, sim float64
	}{
		{"colonist deaths", rs.ColonistDeaths.Mean, ss.ColonistDeaths.Mean},
		{"colonist downs", rs.ColonistDowns.Mean, ss.ColonistDowns.Mean},
	} {
		if tol := tolerance(m.real); math.Abs(m.sim-m.real) > tol {
			r.Gaps = append(r.Gaps, Gap{Fixture: fr.Name, Variant: v.Name, Measure: m.name, Real: m.real, Sim: m.sim, Tolerance: tol})
		}
	}
	if ss.SeedsWithColonistDeath == 0 && rs.SeedsWithColonistDeath > 0 {
		r.FalseSafe = append(r.FalseSafe, fr.Name+"/"+v.Name)
	}
}

// rank compares the sim's and the real sweep's ordering of the variants of a
// held-out fixture. It returns the pairs the real sweep orders and how many
// the sim orders the same way, and records survival-critical misrankings and
// the regret of following the sim's pick.
func (r *Report) rank(fixture string, vs []VariantInput) (pairs, agreed int) {
	for i := range vs {
		for j := i + 1; j < len(vs); j++ {
			real := better(vs[i].Real.Summary, vs[j].Real.Summary)
			sim := better(vs[i].Sim.Summary, vs[j].Sim.Summary)
			if real != 0 {
				pairs++
				if sim == real {
					agreed++
				}
			}
			if sim == 0 {
				continue
			}
			pref, other := vs[i], vs[j]
			if sim > 0 {
				pref, other = other, pref
			}
			if pd, od := pref.Real.Summary.ColonistDeaths.Mean, other.Real.Summary.ColonistDeaths.Mean; pd > od {
				r.Misrankings = append(r.Misrankings, Misranking{Fixture: fixture, Preferred: pref.Name, Other: other.Name, RealDeathsPreferred: pd, RealDeathsOther: od})
			}
		}
	}
	pick, best := vs[0], vs[0]
	for _, v := range vs[1:] {
		if better(v.Sim.Summary, pick.Sim.Summary) < 0 {
			pick = v
		}
		if better(v.Real.Summary, best.Real.Summary) < 0 {
			best = v
		}
	}
	p, b := pick.Real.Summary, best.Real.Summary
	r.Regrets = append(r.Regrets, Regret{
		Fixture: fixture, SimPick: pick.Name, RealBest: best.Name,
		Deaths: p.ColonistDeaths.Mean - b.ColonistDeaths.Mean,
		Downs:  p.ColonistDowns.Mean - b.ColonistDowns.Mean,
		Damage: p.ColonistDamage.Mean - b.ColonistDamage.Mean,
	})
	return pairs, agreed
}
