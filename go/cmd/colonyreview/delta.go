package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"sort"
)

// The nightly comparison (#1936, epic #1852): a run's score against the
// previous night's stored score for the same seed. The store is the earlier
// run dirs (run.json each) that the workflow fetches from the previous
// reports, the same ones the Pages site is built from. One run per seed
// makes a delta noisy, so it is a signal: it gates nothing. A missing
// baseline, or a component either side could not read, is unknown, never a
// zero.

// Delta statuses.
const (
	deltaOK         = "ok"          // both runs have the component's score
	deltaUnknown    = "unknown"     // a side has no score (null value, or the component is absent there)
	deltaNoBaseline = "no_baseline" // no earlier run with this seed and a score
)

// ComponentDelta is one component's change from the baseline. Delta is in
// score units (0-1, positive is better) and ValueDelta in the component's
// own unit; both are nil unless Status is ok.
type ComponentDelta struct {
	Name       string   `json:"name"`
	Status     string   `json:"status"`
	Delta      *float64 `json:"delta"`
	ValueDelta *float64 `json:"value_delta"`
}

// Delta is HEAD's score minus the baseline's. Scalar is in scalar points
// (nil when either scalar is unknown).
type Delta struct {
	Status         string           `json:"status"`
	Baseline       string           `json:"baseline,omitempty"` // the baseline run dir name
	BaselineCommit string           `json:"baseline_commit,omitempty"`
	Scalar         *float64         `json:"scalar"`
	Components     []ComponentDelta `json:"components,omitempty"`
}

func sub(a, b *float64) *float64 {
	if a == nil || b == nil {
		return nil
	}
	d := *a - *b
	return &d
}

// Compare is cur's score against base's. Components are listed in cur's
// order, then any only the baseline had.
func Compare(cur, base Score) Delta {
	d := Delta{Status: deltaOK, Scalar: sub(cur.Scalar, base.Scalar)}
	have := map[string]Component{}
	for _, c := range base.Components {
		have[c.Name] = c
	}
	seen := map[string]bool{}
	for _, c := range cur.Components {
		seen[c.Name] = true
		cd := ComponentDelta{Name: c.Name, Status: deltaUnknown}
		if b, ok := have[c.Name]; ok {
			if cd.Delta = sub(c.Score, b.Score); cd.Delta != nil {
				cd.Status = deltaOK
				cd.ValueDelta = sub(c.Value, b.Value)
			}
		}
		d.Components = append(d.Components, cd)
	}
	for _, c := range base.Components {
		if !seen[c.Name] {
			d.Components = append(d.Components, ComponentDelta{Name: c.Name, Status: deltaUnknown})
		}
	}
	return d
}

// FindBaseline is the newest run dir under runs, other than self, whose
// run.json carries the same seed and a score; ok is false when none does.
// Dir names sort by date then run id, so the greatest earlier name is the
// previous night's.
func FindBaseline(runs, self, seed string) (name string, s Summary, ok bool) {
	entries, err := os.ReadDir(runs)
	if err != nil {
		return "", s, false
	}
	var names []string
	for _, e := range entries {
		if e.IsDir() && e.Name() < self {
			names = append(names, e.Name())
		}
	}
	sort.Sort(sort.Reverse(sort.StringSlice(names)))
	for _, n := range names {
		data, err := os.ReadFile(filepath.Join(runs, n, "run.json"))
		if err != nil {
			continue
		}
		var c Summary
		if json.Unmarshal(data, &c) != nil || c.Meta["seed"] != seed || len(c.Score.Components) == 0 {
			continue
		}
		return n, c, true
	}
	return "", s, false
}

// CompareToStore sets s.Delta against the previous night's run for the
// same seed found under runs (self is this run's dir name). An empty runs
// skips the comparison.
func CompareToStore(s *Summary, runs, self string) {
	if runs == "" {
		return
	}
	name, base, ok := FindBaseline(runs, self, s.Meta["seed"])
	if !ok {
		s.Delta = &Delta{Status: deltaNoBaseline}
		return
	}
	d := Compare(s.Score, base.Score)
	d.Baseline, d.BaselineCommit = name, base.Meta["commit"]
	s.Delta = &d
}
