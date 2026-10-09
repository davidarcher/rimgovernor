package main

import (
	"fmt"
	"math"
)

// The colony score is a vector of components read from
// the run timeline plus a weighted scalar for ranking. Signal only: it gates
// nothing. A component the timeline cannot supply is unknown (a nil Value)
// and is left out of the scalar, never counted as zero.

// Normalisation anchors: a component's Score is its Value mapped onto [0, 1]
// (1 best) against these, then clamped. Documented in
// docs/developers/testing/colony-review.md.
const (
	horizonDays      = 15.0 // one in-game season (quadrum): the stage-days scale
	wealthGrowthFull = 1.0  // wealth doubling over the run scores 1
	foodRunwayFull   = 10.0 // a ten-day minimum runway scores 1
)

// Weights of the scalar. A stage tier's weight is weightStages split evenly
// over the tiers the run reached.
const (
	weightWealth = 3.0
	weightMood   = 2.0
	weightFood   = 3.0
	weightDeaths = 4.0
	weightDowned = 1.0
	weightStages = 3.0
	weightRaid   = 2.0
)

// Component is one dimension of the score. Value is in Unit; nil means the
// timeline could not supply it, and then Score is nil too and the scalar
// ignores the component.
type Component struct {
	Name   string   `json:"name"`
	Unit   string   `json:"unit"`
	Weight float64  `json:"weight"`
	Value  *float64 `json:"value"`
	Score  *float64 `json:"score"`
	Note   string   `json:"note,omitempty"`
}

// Score is the vector and its weighted scalar (0-100, nil when no component
// is known).
type Score struct {
	Components []Component `json:"components"`
	Scalar     *float64    `json:"scalar"`
}

func clamp01(x float64) float64 { return math.Max(0, math.Min(1, x)) }

func (c *Component) set(value, score float64) {
	s := clamp01(score)
	c.Value, c.Score = &value, &s
}

// ComputeScore derives the score from the run's rows.
func ComputeScore(rows []Row) Score {
	var read []Row // rows with a census reading
	for _, r := range rows {
		if r.Census.Error == "" && r.Census.Colonists != nil {
			read = append(read, r)
		}
	}
	mk := func(name, unit string, w float64, note string) Component {
		return Component{Name: name, Unit: unit, Weight: w, Note: note}
	}
	wealth := mk("wealth_growth", "ratio", weightWealth, "last wealth over first, minus 1")
	mood := mk("mean_mood", "0-1", weightMood, "mean of the hourly colony mood mean")
	food := mk("min_food_runway", "days", weightFood, "lowest food runway of the run")
	deaths := mk("deaths", "colonists", weightDeaths, "colonists lost between readings")
	downed := mk("downed_time", "fraction of hours", weightDowned, "readings with a colonist downed")
	raid := mk("raid_damage", "", weightRaid, "the timeline records no raid damage")

	var wFirst, wLast float64
	wN := 0
	var moodSum float64
	var moodN int
	minFood := math.Inf(1)
	lost, downedHours := 0, 0
	for i, r := range read {
		c := r.Census
		if c.WealthTotal != nil {
			if wN == 0 {
				wFirst = *c.WealthTotal
			}
			wLast = *c.WealthTotal
			wN++
		}
		if c.MoodMean != nil {
			moodSum += *c.MoodMean
			moodN++
		}
		if c.FoodRunwayDays != nil {
			minFood = math.Min(minFood, *c.FoodRunwayDays)
		}
		if i > 0 && colonists(r) < colonists(read[i-1]) {
			lost += colonists(read[i-1]) - colonists(r)
		}
		anyDowned := c.Downed > 0
		for _, p := range c.Pawns {
			anyDowned = anyDowned || (p.Downed != nil && *p.Downed)
		}
		if anyDowned {
			downedHours++
		}
	}
	if wN > 1 && wFirst > 0 {
		g := wLast/wFirst - 1
		wealth.set(g, g/wealthGrowthFull)
	}
	if moodN > 0 {
		m := moodSum / float64(moodN)
		mood.set(m, m)
	}
	if !math.IsInf(minFood, 1) {
		food.set(minFood, minFood/foodRunwayFull)
	}
	if len(read) > 0 && colonists(read[0]) > 0 {
		deaths.set(float64(lost), 1-float64(lost)/float64(colonists(read[0])))
	}
	if len(read) > 0 {
		f := float64(downedHours) / float64(len(read))
		downed.set(f, 1-f)
	}

	out := []Component{wealth, mood, food, deaths, downed}
	out = append(out, stageComponents(read)...)
	out = append(out, raid)

	var sum, weight float64
	for _, c := range out {
		if c.Score != nil {
			sum += c.Weight * *c.Score
			weight += c.Weight
		}
	}
	s := Score{Components: out}
	if weight > 0 {
		v := 100 * sum / weight
		s.Scalar = &v
	}
	return s
}

// stageComponents is one component per tech tier the run reached: the days
// from the start to its first reading. Without a tier reading it is a single
// unknown component.
func stageComponents(read []Row) []Component {
	var order []string
	first := map[string]float64{}
	for _, r := range read {
		if r.Census.TechTier == nil || *r.Census.TechTier == "" {
			continue
		}
		t := *r.Census.TechTier
		if _, seen := first[t]; !seen {
			order = append(order, t)
			first[t] = float64(r.Tick) / 60000
		}
	}
	if len(order) == 0 {
		return []Component{{Name: "stage_days", Unit: "days", Weight: weightStages, Note: "the timeline has no tech tier reading"}}
	}
	var out []Component
	for _, t := range order {
		c := Component{Name: "stage_days:" + t, Unit: "days", Weight: weightStages / float64(len(order)),
			Note: fmt.Sprintf("days until the colony first reached the %s tech tier", t)}
		c.set(first[t], 1-first[t]/horizonDays)
		out = append(out, c)
	}
	return out
}
