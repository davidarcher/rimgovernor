package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Shadow project ranker (#1913, epic #1856). It scores each candidate
// development goal by projected shortfall days per open plan action and
// records where that order disagrees with the live ranking
// (RankDevelopment: capacity, dependency donation, waiting age, overcommit
// deferral). Shadow only: RoutineReview.ShadowRank is read by no admission,
// and ShadowRankOf takes the DevelopmentState by value and never writes it.
// No cutover without the colony-score A/B (decided on #1856).

// ShadowDomain is the projected resource a goal's work protects.
type ShadowDomain string

const (
	ShadowFood        ShadowDomain = "food"
	ShadowPower       ShadowDomain = "power"
	ShadowTemperature ShadowDomain = "temperature"
	ShadowDefense     ShadowDomain = "defense"
)

// shadowDomains is the goal-to-domain table (David, #1913 comment). A goal
// outside it is left out of the ranking, never defaulted.
var shadowDomains = map[ConcernID]ShadowDomain{
	MaintainResource:        ShadowFood,
	MaintainFoodStorage:     ShadowFood,
	MaintainRefrigeration:   ShadowPower,
	EnsureTemperatureSafety: ShadowTemperature,
	EnsureBasicDefense:      ShadowDefense,
}

// ForwardObserved are the projector inputs the routine facts do not already
// carry (food, sleeping range and conditions are read from the facts).
type ForwardObserved struct {
	Power   []PowerNetworkFact                `json:",omitempty"`
	Turrets domain.Fact[[]DefenseTurretFacts] `json:",omitzero"`
}

// ForwardInputsOf assembles the projector inputs from the routine facts.
func ForwardInputsOf(f RoutineFacts, p RoutinePolicy) ForwardInputs {
	in := ForwardInputs{Power: f.Forward.Power, Sleeping: SleepingRange{Min: f.SleepingMin, Max: f.SleepingMax}, Conditions: f.DisasterConditions, Turrets: f.Forward.Turrets, Policy: p}
	if supply, known := f.AnimalUpkeep.Food.Value(); known {
		in.Food = supply
	}
	return in
}

type ShadowEntry struct {
	Goal          ConcernID
	Domain        ShadowDomain
	ShortfallDays float64
	// OpenActions is the goal's open plan action count, the labor cost.
	OpenActions int
	// Score is ShortfallDays per open action.
	Score float64
}

// ShadowDisagreement is a ranked goal whose 1-based place among the ranked
// goals differs between the live order and the shadow order.
type ShadowDisagreement struct {
	Goal    ConcernID
	Current int
	Shadow  int
	// Reason is the live row's reason (empty while it is selected).
	Reason DevelopmentReason `json:",omitempty"`
}

type ShadowUnranked struct {
	Goal   ConcernID
	Reason string
}

type ShadowRank struct {
	HorizonDays float64
	// Ranked is the shadow order, best first.
	Ranked []ShadowEntry `json:",omitempty"`
	// Current is the live order of the same goals.
	Current       []ConcernID          `json:",omitempty"`
	Disagreements []ShadowDisagreement `json:",omitempty"`
	Unranked      []ShadowUnranked     `json:",omitempty"`
}

// shadowCandidate: a row still contending for a slot, not one already under
// way, cancelled, blocked or held by emergency or stage.
func shadowCandidate(r DevelopmentRow) bool {
	switch r.Reason {
	case "", DevelopmentCapacity, DevelopmentLabor, DevelopmentOvercommitted:
		return true
	}
	return false
}

func shadowShortfall(p ForwardProjection, d ShadowDomain) (float64, string) {
	switch d {
	case ShadowFood:
		if v, ok := p.Food.Value(); ok {
			return v.ShortfallDays, ""
		}
	case ShadowPower:
		if v, ok := p.Power.Value(); ok {
			return v.ShortfallDays, ""
		}
	case ShadowTemperature:
		if v, ok := p.Temperature.Value(); ok {
			return v.BreachDays, ""
		}
	case ShadowDefense:
		// The defense projection lists burn rate and raid arrival as gaps: it
		// carries no shortfall to credit.
		if _, ok := p.Defense.Value(); ok {
			return 0, "defense projection has no shortfall"
		}
	}
	return 0, "projection unknown"
}

// ShadowRankOf ranks the state's candidate rows by projected shortfall per
// open action. openActions is each goal's open plan action count.
func ShadowRankOf(s DevelopmentState, projection ForwardProjection, openActions map[ConcernID]int) ShadowRank {
	out := ShadowRank{HorizonDays: projection.HorizonDays}
	var current []ConcernID
	rows := map[ConcernID]DevelopmentRow{}
	for _, row := range s.Rows {
		if !shadowCandidate(row) {
			continue
		}
		d, mapped := shadowDomains[row.Goal]
		if !mapped {
			continue
		}
		rows[row.Goal] = row
		days, why := shadowShortfall(projection, d)
		if why == "" && openActions[row.Goal] <= 0 {
			why = "no open plan action"
		}
		if why != "" {
			out.Unranked = append(out.Unranked, ShadowUnranked{Goal: row.Goal, Reason: why})
			continue
		}
		n := openActions[row.Goal]
		out.Ranked = append(out.Ranked, ShadowEntry{Goal: row.Goal, Domain: d, ShortfallDays: days, OpenActions: n, Score: math.RoundToEven(days/float64(n)*1000) / 1000})
		current = append(current, row.Goal)
	}
	sort.Slice(out.Ranked, func(i, j int) bool {
		a, b := out.Ranked[i], out.Ranked[j]
		if a.Score != b.Score {
			return a.Score > b.Score
		}
		return a.Goal < b.Goal
	})
	out.Current = current
	shadow := map[ConcernID]int{}
	for i, e := range out.Ranked {
		shadow[e.Goal] = i + 1
	}
	for i, g := range current {
		if shadow[g] != i+1 {
			out.Disagreements = append(out.Disagreements, ShadowDisagreement{Goal: g, Current: i + 1, Shadow: shadow[g], Reason: rows[g].Reason})
		}
	}
	return out
}
