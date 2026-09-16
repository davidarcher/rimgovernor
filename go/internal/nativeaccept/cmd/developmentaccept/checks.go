package main

import (
	"encoding/json"
	"fmt"
	"sort"
	"time"
)

// Sample is one /api/routines read of the recorded development ranking,
// decoded from the wire shape httpapi.routineDevelopmentDTO documents.
type Sample struct {
	At          time.Time    `json:"at"`
	Phase       string       `json:"phase"`
	ReviewTick  *int64       `json:"lastReviewTick"`
	Development *Development `json:"development"`
}
type Development struct {
	Tick      int64      `json:"tick"`
	Workers   *int       `json:"workers"`
	Labor     []LaborRow `json:"labor"`
	Capacity  int        `json:"capacity"`
	Committed []string   `json:"committed"`
	Rows      []Row      `json:"rows"`
}
type LaborRow struct {
	Work string `json:"work"`
	Free int    `json:"free"`
}
type Row struct {
	Goal         string   `json:"goal"`
	Score        float64  `json:"score"`
	Deficit      *float64 `json:"deficit"`
	Risk         *float64 `json:"risk"`
	WaitingSince int64    `json:"waitingSince"`
	Selected     bool     `json:"selected"`
	Committed    bool     `json:"committed"`
	Reason       string   `json:"reason"`
	Bottleneck   string   `json:"bottleneck"`
}

func decodeSample(v map[string]any, phase string, at time.Time) (Sample, error) {
	data, err := json.Marshal(v)
	if err != nil {
		return Sample{}, err
	}
	s := Sample{At: at, Phase: phase}
	if err := json.Unmarshal(data, &s); err != nil {
		return Sample{}, fmt.Errorf("decode /api/routines: %w", err)
	}
	return s, nil
}

// checkSample returns every invariant this sample violates. limit is
// serve's --routine-project-limit; researchTarget non-empty means the
// review carried a research target whose deficit must be measured, never
// unknown.
func checkSample(s Sample, limit int, researchTarget string) []string {
	d := s.Development
	if d == nil {
		return nil
	}
	var bad []string
	admitted := len(d.Committed)
	free := map[string]int{}
	for _, l := range d.Labor {
		free[l.Work] = l.Free
	}
	for _, r := range d.Rows {
		if r.Selected {
			admitted++
		}
		switch {
		case r.Selected && (r.Committed || r.Reason != ""):
			bad = append(bad, fmt.Sprintf("%s: selected with reason %q", r.Goal, r.Reason))
		case !r.Selected && !r.Committed && r.Reason == "":
			bad = append(bad, fmt.Sprintf("%s: deferred without a reason", r.Goal))
		}
		if r.Reason == "labor_unavailable" {
			if _, known := free[r.Bottleneck]; r.Bottleneck == "" || !known {
				bad = append(bad, fmt.Sprintf("%s: labor_unavailable without a censused bottleneck (%q)", r.Goal, r.Bottleneck))
			}
		} else if r.Bottleneck != "" {
			bad = append(bad, fmt.Sprintf("%s: bottleneck %q on reason %q", r.Goal, r.Bottleneck, r.Reason))
		}
		if len(d.Labor) > 0 && r.Reason == "workers_unknown" {
			bad = append(bad, fmt.Sprintf("%s: workers_unknown despite a labor census", r.Goal))
		}
		if researchTarget != "" && r.Goal == "EnsureResearch" && (r.Reason == "deficit_unknown" || r.Deficit == nil) {
			bad = append(bad, "EnsureResearch: deficit unknown although a research target was configured (review-time research read missing)")
		}
		if r.WaitingSince > d.Tick {
			bad = append(bad, fmt.Sprintf("%s: waitingSince %d after review tick %d", r.Goal, r.WaitingSince, d.Tick))
		}
	}
	if admitted > d.Capacity {
		bad = append(bad, fmt.Sprintf("admitted %d beyond capacity %d", admitted, d.Capacity))
	}
	if d.Capacity > limit {
		bad = append(bad, fmt.Sprintf("capacity %d beyond project limit %d", d.Capacity, limit))
	}
	if d.Workers != nil && d.Capacity > *d.Workers {
		bad = append(bad, fmt.Sprintf("capacity %d beyond %d workers", d.Capacity, *d.Workers))
	}
	if d.Workers == nil && d.Capacity != 0 {
		bad = append(bad, fmt.Sprintf("capacity %d with unknown workers", d.Capacity))
	}
	return bad
}

// checkRestart compares the last ranking recorded before a controller was
// killed with the first one its replacement recorded: goals still waiting
// keep their waiting age, and the reviewed tick never rewinds.
func checkRestart(before, after Development) []string {
	var bad []string
	if after.Tick < before.Tick {
		bad = append(bad, fmt.Sprintf("review tick rewound %d -> %d across restart", before.Tick, after.Tick))
	}
	prior := map[string]Row{}
	for _, r := range before.Rows {
		prior[r.Goal] = r
	}
	for _, r := range after.Rows {
		p, ok := prior[r.Goal]
		if !ok || p.Selected || p.Committed || r.Selected || r.Committed {
			continue
		}
		if r.WaitingSince != p.WaitingSince {
			bad = append(bad, fmt.Sprintf("%s: waiting age rewritten across restart %d -> %d", r.Goal, p.WaitingSince, r.WaitingSince))
		}
	}
	return bad
}

// Metrics summarises a timeline for the report: per-goal admission counts,
// reason histogram and the longest consecutive deferral in ticks.
type Metrics struct {
	Samples        int              `json:"samples"`
	Ranked         int              `json:"samples_with_ranking"`
	Reviews        int              `json:"distinct_reviews"`
	Goals          []string         `json:"goals_seen"`
	Selected       map[string]int   `json:"selected_by_goal"`
	Committed      map[string]int   `json:"committed_by_goal"`
	Reasons        map[string]int   `json:"reasons"`
	LongestWait    map[string]int64 `json:"longest_wait_ticks"`
	ResearchRanked bool             `json:"research_ranked"`
}

func deriveMetrics(samples []Sample) Metrics {
	m := Metrics{Selected: map[string]int{}, Committed: map[string]int{}, Reasons: map[string]int{}, LongestWait: map[string]int64{}}
	goals := map[string]bool{}
	var lastTick int64 = -1
	for _, s := range samples {
		m.Samples++
		d := s.Development
		if d == nil {
			continue
		}
		m.Ranked++
		if d.Tick != lastTick {
			m.Reviews++
			lastTick = d.Tick
		}
		for _, r := range d.Rows {
			goals[r.Goal] = true
			if r.Goal == "EnsureResearch" {
				m.ResearchRanked = true
			}
			switch {
			case r.Selected:
				m.Selected[r.Goal]++
			case r.Committed:
				m.Committed[r.Goal]++
			default:
				m.Reasons[r.Reason]++
				if wait := d.Tick - r.WaitingSince; wait > m.LongestWait[r.Goal] {
					m.LongestWait[r.Goal] = wait
				}
			}
		}
	}
	for g := range goals {
		m.Goals = append(m.Goals, g)
	}
	sort.Strings(m.Goals)
	return m
}
