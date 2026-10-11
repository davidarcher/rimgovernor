package calibrate

import (
	"encoding/json"
	"fmt"
	"strings"
)

// Marshal is the committed form of a report.
func (r Report) Marshal() ([]byte, error) { return json.MarshalIndent(r, "", "  ") }

// ParseReport reads a committed report.
func ParseReport(data []byte) (Report, error) {
	var r Report
	err := json.Unmarshal(data, &r)
	return r, err
}

// ExitCode is the command's verdict: 0 for a pass, 1 for a fail.
func (r Report) ExitCode() int {
	if r.Pass {
		return 0
	}
	return 1
}

// largestGaps is how many gaps the text names.
const largestGaps = 5

// Text is the human report: the verdict and thresholds first, then every
// failure, the largest gaps, regret and false-safe variants, then the means.
func (r Report) Text() string {
	var b strings.Builder
	verdict := "FAIL"
	if r.Pass {
		verdict = "PASS"
	}
	fmt.Fprintf(&b, "combatsim calibration gate (ranged slice): %s\n", verdict)
	fmt.Fprintf(&b, "stamp: assembly %s, rules version %d, thresholds version %d, sweep schema %d\n",
		r.Stamp.AssemblyHash, r.Stamp.RulesVersion, r.Stamp.ThresholdsVersion, r.Stamp.SweepVersion)
	fmt.Fprintf(&b, "thresholds: rank agreement >= %.2f on held-out fixtures; %d survival-critical misrankings; colonist deaths and downs within +/-%.0f%% of the real mean or +/-%.0f pawn\n",
		MinRankAgreement, MaxMisrankings, RelTolerance*100, SmallMeanPawns)
	fmt.Fprintf(&b, "rank agreement: %.2f over %d pairs; survival-critical misrankings: %d\n", r.RankAgreement, r.RankPairs, len(r.Misrankings))
	for _, f := range r.Failures {
		fmt.Fprintf(&b, "  FAILURE: %s\n", f)
	}
	if len(r.Gaps) > 0 {
		b.WriteString("largest gaps (sim mean vs real mean, in tolerances):\n")
		for i, g := range r.Gaps {
			if i == largestGaps {
				break
			}
			fmt.Fprintf(&b, "  %s/%s %s: sim %.2f, real %.2f, %.1fx the +/-%.2f tolerance\n", g.Fixture, g.Variant, g.Measure, g.Sim, g.Real, g.Excess(), g.Tolerance)
		}
	}
	for _, g := range r.Regrets {
		fmt.Fprintf(&b, "native regret %s: following the sim picks %s, the real best is %s; deaths %+.2f, downs %+.2f, damage %+.1f\n", g.Fixture, g.SimPick, g.RealBest, g.Deaths, g.Downs, g.Damage)
	}
	if len(r.Regrets) == 0 {
		b.WriteString("native regret: not measured (no held-out ranking)\n")
	}
	if len(r.FalseSafe) == 0 {
		b.WriteString("false-safe variants (sim sees no colonist death, real does): none\n")
	} else {
		fmt.Fprintf(&b, "false-safe variants (sim sees no colonist death, real does): %s\n", strings.Join(r.FalseSafe, ", "))
	}
	b.WriteString("fixtures:\n")
	for _, f := range r.Fixtures {
		fmt.Fprintf(&b, "  %s (%s): %s\n", f.Name, f.Role, f.Status)
		for _, c := range f.Cells {
			fmt.Fprintf(&b, "    %-14s deaths real %.2f sim %.2f (seeds with a death real %d sim %d) | downs real %.2f sim %.2f | damage real %.1f sim %.1f\n",
				c.Variant, c.RealDeaths, c.SimDeaths, c.RealDeathSeeds, c.SimDeathSeeds, c.RealDowns, c.SimDowns, c.RealDamage, c.SimDamage)
		}
	}
	return b.String()
}
