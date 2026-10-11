package combatmetrics

import (
	"encoding/json"
	"fmt"
	"math"
	"slices"
	"strings"
)

// SweepVersion is the Sweep schema version: bump it when a field changes
// meaning. The baselines (#2843) are Sweep files, so a reader refuses one of
// another version instead of comparing it.
const SweepVersion = 1

// SeedRow is one seed's run: its scored metrics and the evidence that the
// run started where its pair did.
type SeedRow struct {
	Seed   int `json:"seed"`
	Worker int `json:"worker"`
	// StagedDigest is the digest the staging read back; the same checkpoint
	// reads the same digest for every seed.
	StagedDigest string `json:"stagedDigest,omitempty"`
	// RunSeedDraws are the next draws read right after the reseed: the same
	// seed on the same checkpoint reads the same draws.
	RunSeedDraws []int    `json:"runSeedDraws,omitempty"`
	Error        string   `json:"error,omitempty"`
	Metrics      *Metrics `json:"metrics,omitempty"`
}

// Sweep is one fixture run under N seeds: a row per seed and the derived
// summary. It is the file `acceptance sweep` writes and `-against` reads.
type Sweep struct {
	Version int    `json:"version"`
	Fixture string `json:"fixture"`
	// Workers and Load are the concurrency and box-load note the runs
	// shared; WallMs depends on both and is excluded from comparisons.
	Workers int       `json:"workers"`
	Load    string    `json:"load"`
	WallMs  int64     `json:"wallMs"`
	Rows    []SeedRow `json:"rows"`
	Summary Summary   `json:"summary"`
}

// Stat is the mean of a measure over N runs with a 95% t interval. The
// interval is undefined (Lo and Hi equal the mean) below two runs; a measure
// with no variance has a zero-width interval.
type Stat struct {
	N    int     `json:"n"`
	Mean float64 `json:"mean"`
	SD   float64 `json:"sd"`
	Lo   float64 `json:"lo"`
	Hi   float64 `json:"hi"`
}

// Summary is the distribution a Sweep reports. The survival counts come
// first: deaths and downs are never reduced to one scalar.
type Summary struct {
	Runs                   int `json:"runs"`
	SeedsWithColonistDeath int `json:"seedsWithColonistDeath"`
	SeedsWithColonistDown  int `json:"seedsWithColonistDown"`
	Unresolved             int `json:"unresolved"`

	ColonistDeaths      Stat `json:"colonistDeaths"`
	ColonistDowns       Stat `json:"colonistDowns"`
	ColonistDamage      Stat `json:"colonistDamage"`
	EnemyDowns          Stat `json:"enemyDowns"`
	EnemyDeaths         Stat `json:"enemyDeaths"`
	ContactToResolution Stat `json:"contactToResolutionTicks"` // resolved runs only
}

// measure is a named per-run number; ok is false where the run has none.
type measure struct {
	name string
	get  func(Metrics) (float64, bool)
}

// measures lists deaths and downs first; Compare gates on the first two.
var measures = []measure{
	{"colonistDeaths", func(m Metrics) (float64, bool) { return float64(m.ColonistDeaths), true }},
	{"colonistDowns", func(m Metrics) (float64, bool) { return float64(m.ColonistDowns), true }},
	{"colonistDamage", func(m Metrics) (float64, bool) { return m.ColonistDamage, true }},
	{"enemyDowns", func(m Metrics) (float64, bool) { return float64(m.EnemyDowns), true }},
	{"enemyDeaths", func(m Metrics) (float64, bool) { return float64(m.EnemyDeaths), true }},
	{"contactToResolutionTicks", func(m Metrics) (float64, bool) {
		return float64(m.ContactToResolution), m.ContactToResolution >= 0
	}},
}

// tCrit95 is the two-sided 95% Student t critical value by degrees of
// freedom 1..30; beyond that the normal 1.96 is within 2%.
var tCrit95 = [...]float64{
	12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
	2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,
	2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042,
}

// NewStat is the mean and 95% t interval of xs.
func NewStat(xs []float64) Stat {
	s := Stat{N: len(xs)}
	if len(xs) == 0 {
		return s
	}
	for _, x := range xs {
		s.Mean += x
	}
	s.Mean /= float64(len(xs))
	s.Lo, s.Hi = s.Mean, s.Mean
	if len(xs) < 2 {
		return s
	}
	var ss float64
	for _, x := range xs {
		ss += (x - s.Mean) * (x - s.Mean)
	}
	s.SD = math.Sqrt(ss / float64(len(xs)-1))
	t := 1.96
	if df := len(xs) - 1; df <= len(tCrit95) {
		t = tCrit95[df-1]
	}
	half := t * s.SD / math.Sqrt(float64(len(xs)))
	s.Lo, s.Hi = s.Mean-half, s.Mean+half
	return s
}

// Summarize derives a Sweep's summary from its rows; a row that failed has no
// metrics and counts in no measure.
func Summarize(rows []SeedRow) Summary {
	var out Summary
	series := make([][]float64, len(measures))
	for _, row := range rows {
		if row.Metrics == nil {
			continue
		}
		m := *row.Metrics
		out.Runs++
		if m.ColonistDeaths > 0 {
			out.SeedsWithColonistDeath++
		}
		if m.ColonistDowns > 0 {
			out.SeedsWithColonistDown++
		}
		if m.Winner == "" {
			out.Unresolved++
		}
		for i, ms := range measures {
			if v, ok := ms.get(m); ok {
				series[i] = append(series[i], v)
			}
		}
	}
	out.ColonistDeaths, out.ColonistDowns, out.ColonistDamage = NewStat(series[0]), NewStat(series[1]), NewStat(series[2])
	out.EnemyDowns, out.EnemyDeaths, out.ContactToResolution = NewStat(series[3]), NewStat(series[4]), NewStat(series[5])
	return out
}

// NewSweep assembles a Sweep from its rows, sorted by seed.
func NewSweep(fixture string, workers int, load string, wallMs int64, rows []SeedRow) Sweep {
	rows = slices.Clone(rows)
	slices.SortFunc(rows, func(a, b SeedRow) int { return a.Seed - b.Seed })
	return Sweep{Version: SweepVersion, Fixture: fixture, Workers: workers, Load: load, WallMs: wallMs, Rows: rows, Summary: Summarize(rows)}
}

// ParseSweep reads a Sweep file, refusing another schema version.
func ParseSweep(data []byte) (Sweep, error) {
	var s Sweep
	if err := json.Unmarshal(data, &s); err != nil {
		return s, err
	}
	if s.Version != SweepVersion {
		return s, fmt.Errorf("sweep is schema version %d, this build reads version %d: re-run the sweep", s.Version, SweepVersion)
	}
	return s, nil
}

// Delta is a paired difference (candidate minus baseline) over the seeds both
// sweeps ran.
type Delta struct {
	Measure string `json:"measure"`
	Stat    Stat   `json:"delta"`
}

// Comparison is a candidate sweep against a baseline sweep, seed by seed.
type Comparison struct {
	Fixture  string  `json:"fixture"`
	Paired   []int   `json:"pairedSeeds"`
	Unpaired []int   `json:"unpairedSeeds,omitempty"` // in one sweep only, or failed in either
	Deltas   []Delta `json:"deltas"`
	// DeathGainSeeds are the seeds where the candidate lost more colonists
	// than the baseline did.
	DeathGainSeeds []int `json:"deathGainSeeds,omitempty"`
	// Regressions names why the verdict is a regression; empty is a pass.
	Regressions []string `json:"regressions,omitempty"`
}

// Regressed is the exit verdict: a survival regression.
func (c Comparison) Regressed() bool { return len(c.Regressions) > 0 }

// Compare pairs the candidate against the baseline by seed. It regresses when
// a seed gains a colonist death, or when the paired delta interval of colonist
// deaths or downs lies wholly above zero (a zero-variance positive delta
// included; one pair has no interval). Damage, enemy outcomes and time are
// reported, never gated.
func Compare(base, cand Sweep) (Comparison, error) {
	out := Comparison{Fixture: cand.Fixture}
	if base.Fixture != cand.Fixture {
		return out, fmt.Errorf("baseline is fixture %q, candidate %q", base.Fixture, cand.Fixture)
	}
	byseed := map[int]SeedRow{}
	for _, r := range base.Rows {
		byseed[r.Seed] = r
	}
	seen := map[int]bool{}
	var pairs [][2]Metrics
	for _, r := range cand.Rows {
		seen[r.Seed] = true
		b, ok := byseed[r.Seed]
		if !ok || b.Metrics == nil || r.Metrics == nil {
			out.Unpaired = append(out.Unpaired, r.Seed)
			continue
		}
		out.Paired = append(out.Paired, r.Seed)
		pairs = append(pairs, [2]Metrics{*b.Metrics, *r.Metrics})
		if r.Metrics.ColonistDeaths > b.Metrics.ColonistDeaths {
			out.DeathGainSeeds = append(out.DeathGainSeeds, r.Seed)
		}
	}
	for _, r := range base.Rows {
		if !seen[r.Seed] {
			out.Unpaired = append(out.Unpaired, r.Seed)
		}
	}
	slices.Sort(out.Unpaired)
	if len(pairs) == 0 {
		return out, fmt.Errorf("baseline and candidate share no completed seed")
	}
	for _, ms := range measures {
		var diffs []float64
		for _, p := range pairs {
			b, bok := ms.get(p[0])
			c, cok := ms.get(p[1])
			if bok && cok {
				diffs = append(diffs, c-b)
			}
		}
		out.Deltas = append(out.Deltas, Delta{Measure: ms.name, Stat: NewStat(diffs)})
	}
	if len(out.DeathGainSeeds) > 0 {
		out.Regressions = append(out.Regressions, fmt.Sprintf("colonist deaths rose on seeds %v", out.DeathGainSeeds))
	}
	for _, d := range out.Deltas[:2] { // deaths, downs
		if d.Stat.N >= 2 && d.Stat.Lo > 0 {
			out.Regressions = append(out.Regressions, fmt.Sprintf("%s paired delta %+.2f, 95%% interval [%+.2f, %+.2f] above zero", d.Measure, d.Stat.Mean, d.Stat.Lo, d.Stat.Hi))
		}
	}
	return out, nil
}

func (s Stat) String() string {
	if s.N == 0 {
		return "n/a"
	}
	return fmt.Sprintf("%.2f [%.2f, %.2f] n=%d", s.Mean, s.Lo, s.Hi, s.N)
}

// Report prints the sweep with survival first: the seeds with a colonist
// death or down, the deaths and downs means with intervals, then the rest.
func (s Sweep) Report() string {
	var b strings.Builder
	sum := s.Summary
	fmt.Fprintf(&b, "sweep %s: %d of %d seeds scored, %d workers, %s\n", s.Fixture, sum.Runs, len(s.Rows), s.Workers, s.Load)
	fmt.Fprintf(&b, "  seeds with a colonist death: %d of %d\n", sum.SeedsWithColonistDeath, sum.Runs)
	fmt.Fprintf(&b, "  seeds with a colonist down:  %d of %d\n", sum.SeedsWithColonistDown, sum.Runs)
	fmt.Fprintf(&b, "  unresolved:                  %d of %d\n", sum.Unresolved, sum.Runs)
	fmt.Fprintf(&b, "  colonist deaths: %s\n  colonist downs:  %s\n", sum.ColonistDeaths, sum.ColonistDowns)
	fmt.Fprintf(&b, "  colonist damage: %s\n  enemy downs:     %s\n  enemy deaths:    %s\n  contact to resolution (ticks): %s\n",
		sum.ColonistDamage, sum.EnemyDowns, sum.EnemyDeaths, sum.ContactToResolution)
	for _, r := range s.Rows {
		if r.Error != "" {
			fmt.Fprintf(&b, "  seed %d FAILED: %s\n", r.Seed, r.Error)
		}
	}
	return b.String()
}

// Report prints the paired comparison, deaths and downs first, and the verdict.
func (c Comparison) Report() string {
	var b strings.Builder
	fmt.Fprintf(&b, "paired against baseline, %s: %d seeds paired", c.Fixture, len(c.Paired))
	if len(c.Unpaired) > 0 {
		fmt.Fprintf(&b, ", unpaired %v", c.Unpaired)
	}
	b.WriteString("\n")
	for _, d := range c.Deltas {
		fmt.Fprintf(&b, "  delta %-26s %+.2f [%+.2f, %+.2f] n=%d\n", d.Measure, d.Stat.Mean, d.Stat.Lo, d.Stat.Hi, d.Stat.N)
	}
	if c.Regressed() {
		for _, r := range c.Regressions {
			fmt.Fprintf(&b, "  REGRESSION: %s\n", r)
		}
	} else {
		b.WriteString("  no survival regression\n")
	}
	return b.String()
}
