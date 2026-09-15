package sustainedfood

import "time"

// StallWindow is a contiguous span of samples where EnsureFoodSupply had at
// least one committed method but not a single one of that method's Progress
// rows had left the zero-value "pending" stage -- the exact shape of issue
// #1's originally-reported starvation (Attempt stays 0, Snapshot never
// stamped) generalized to any future recurrence, on this or another variant.
type StallWindow struct {
	StartedAt time.Time `json:"started_at"`
	EndedAt   time.Time `json:"ended_at"`
	Need      string    `json:"need"`
	Samples   int       `json:"samples"`
}

// Metrics summarizes one variant's timeline for the matrix report: the
// per-issue-#1-acceptance-criteria data ("record stock/need trends,
// interruptions, blockers ... and recovery after initial supplies run
// down") that is derivable from the goal-state samples sampleFoodGoal
// already collects, without re-deriving it by hand from a raw timeline in
// every matrix row.
type Metrics struct {
	Samples            int            `json:"samples"`
	NeedCounts         map[string]int `json:"need_counts"`
	StatusCounts       map[string]int `json:"status_counts"`
	DistinctPlans      int            `json:"distinct_plans"`
	Stalls             []StallWindow  `json:"stalls,omitempty"`
	StalledSamples     int            `json:"stalled_samples"`
	StalledFraction    float64        `json:"stalled_fraction"`
	LongestStall       time.Duration  `json:"longest_stall_ns"`
	RecoveredAllStalls bool           `json:"recovered_all_stalls"`
}

// DeriveMetrics computes Metrics from a Run's returned timeline. It never
// errors: a malformed or empty timeline just yields zero-value counts, since
// this is diagnostic bookkeeping, not an acceptance gate itself.
func DeriveMetrics(timeline []map[string]any) Metrics {
	m := Metrics{NeedCounts: map[string]int{}, StatusCounts: map[string]int{}}
	seenPlans := map[string]bool{}
	var stallStart time.Time
	var stallNeed string
	inStall := false
	stallSamples := 0
	flushStall := func(end time.Time) {
		if !inStall {
			return
		}
		duration := end.Sub(stallStart)
		if duration > m.LongestStall {
			m.LongestStall = duration
		}
		m.Stalls = append(m.Stalls, StallWindow{StartedAt: stallStart, EndedAt: end, Need: stallNeed, Samples: stallSamples})
		inStall = false
		stallSamples = 0
	}
	for _, sample := range timeline {
		if _, isErr := sample["error"]; isErr {
			continue
		}
		m.Samples++
		need, _ := sample["need"].(string)
		status, _ := sample["status"].(string)
		if need != "" {
			m.NeedCounts[need]++
		}
		if status != "" {
			m.StatusCounts[status]++
		}
		methodCount, _ := sample["method_count"].(int)
		plans, _ := sample["plans"].([]map[string]any)
		for _, p := range plans {
			if planID, ok := p["plan"].(string); ok {
				seenPlans[planID] = true
			}
		}
		stalled := methodCount > 0 && len(plans) > 0 && allPending(plans)
		at, _ := time.Parse(time.RFC3339, asString(sample["at"]))
		if stalled {
			if !inStall {
				inStall = true
				stallStart = at
				stallNeed = need
			}
			stallSamples++
			m.StalledSamples++
		} else {
			flushStall(at)
		}
	}
	if inStall {
		// Timeline ended still stalled: charge the last sample's timestamp as
		// the (open-ended) stall end so it's still visible in the report,
		// and mark recovery as not confirmed.
		if len(timeline) > 0 {
			if at, err := time.Parse(time.RFC3339, asString(timeline[len(timeline)-1]["at"])); err == nil {
				flushStall(at)
			}
		}
	}
	m.DistinctPlans = len(seenPlans)
	if m.Samples > 0 {
		m.StalledFraction = float64(m.StalledSamples) / float64(m.Samples)
	}
	m.RecoveredAllStalls = true
	if len(m.Stalls) > 0 && len(timeline) > 0 {
		last := m.Stalls[len(m.Stalls)-1]
		if lastAt, err := time.Parse(time.RFC3339, asString(timeline[len(timeline)-1]["at"])); err == nil {
			if !last.EndedAt.Before(lastAt) {
				m.RecoveredAllStalls = false
			}
		}
	}
	return m
}

func allPending(plans []map[string]any) bool {
	for _, p := range plans {
		stages, ok := p["stages"].(map[string]int)
		if !ok {
			return false
		}
		for stage, count := range stages {
			if stage != "pending" && count > 0 {
				return false
			}
		}
	}
	return true
}

func asString(v any) string {
	s, _ := v.(string)
	return s
}
