package policy

import (
	"math"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Project ranker (#1913, cut over in #1914, epic #1856). It scores each
// candidate development goal by projected shortfall days per open plan
// action. RankDevelopment orders the optional goals that carry a projected
// shortfall by it, ahead of the goals the projection cannot score, which
// keep the deficit-and-age order. Startup, emergency and player work never
// enter it: they are not ranked rows.

// ShadowDomain is the projected resource a goal's work protects.
type ShadowDomain string

const (
	ShadowFood        ShadowDomain = "food"
	ShadowPower       ShadowDomain = "power"
	ShadowTemperature ShadowDomain = "temperature"
	ShadowDefense     ShadowDomain = "defense"
	// ShadowConstruction is wood, stone and steel owed to standing and
	// admitted construction against stock (#2365).
	ShadowConstruction ShadowDomain = "construction"
	// ShadowFuel is the fuel generators and empty barrels burn against stock
	// (#2377).
	ShadowFuel ShadowDomain = "fuel"
	// ShadowAnimalFeed is the feed the herd eats against stock and pasture
	// (#2379).
	ShadowAnimalFeed ShadowDomain = "animal_feed"
)

// shadowDomains is the goal-to-domain table (David, #1913 comment; #2365).
// A goal outside it is left out of the ranking, never defaulted. A goal with
// two domains scores the larger known shortfall; it is unranked only when
// every domain is unknown.
var shadowDomains = map[ConcernID][]ShadowDomain{
	MaintainResource:        {ShadowFood, ShadowConstruction, ShadowFuel, ShadowAnimalFeed},
	MaintainFoodStorage:     {ShadowFood},
	MaintainRefrigeration:   {ShadowPower},
	EnsureTemperatureSafety: {ShadowTemperature},
	EnsureBasicDefense:      {ShadowDefense},
	// The construction-bearing Shelter and Industry building concerns.
	MaintainStoneShell:   {ShadowConstruction},
	MaintainHousing:      {ShadowConstruction},
	MaintainShelter:      {ShadowConstruction},
	MaintainHomeCoverage: {ShadowConstruction},
	MaintainFlooring:     {ShadowConstruction},
	MaintainLighting:     {ShadowConstruction},
	MaintainFirebreak:    {ShadowConstruction},
	EnsureBasicPower:     {ShadowConstruction},
	EnsureMechCharger:    {ShadowConstruction},
}

// ForwardObserved are the projector inputs the routine facts do not already
// carry (food, sleeping range and conditions are read from the facts).
type ForwardObserved struct {
	Power   []PowerNetworkFact                `json:",omitempty"`
	Turrets domain.Fact[[]DefenseTurretFacts] `json:",omitzero"`
}

// ForwardInputsOf assembles the projector inputs from the routine facts.
func ForwardInputsOf(f RoundsFacts, p RoundsPolicy) ForwardInputs {
	in := ForwardInputs{Power: f.Forward.Power, Sleeping: SleepingRange{Min: f.SleepingMin, Max: f.SleepingMax}, Conditions: f.DisasterConditions, Turrets: f.Forward.Turrets, Policy: p,
		Construction: ConstructionInputs{Deficit: f.ConstructionDeficit, Admitted: f.Admitted, Stock: f.Resources, Items: f.Items},
		Fuel:         FuelInputs{Consumers: f.Fuel, Stock: StockReader{Resources: f.Resources, Wood: f.Wood}},
		AnimalFeed:   f.animalFeedInputs()}
	if supply, known := f.AnimalUpkeep.Food.Value(); known {
		in.Food = supply
	}
	return in
}

type ShadowEntry struct {
	Concern       ConcernID
	Domain        ShadowDomain
	ShortfallDays float64
	// OpenActions is the goal's open plan action count, the labor cost.
	OpenActions int
	// Score is ShortfallDays per open action.
	Score float64
}

type ShadowUnranked struct {
	Concern ConcernID
	Reason  string
}

type ShadowRank struct {
	// Ranked is the order, best first.
	Ranked   []ShadowEntry
	Unranked []ShadowUnranked
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
	case ShadowConstruction:
		if v, ok := p.Construction.Value(); ok {
			return v.ShortfallDays, ""
		}
	case ShadowFuel:
		if v, ok := p.Fuel.Value(); ok {
			return v.ShortfallDays, ""
		}
	case ShadowAnimalFeed:
		if v, ok := p.AnimalFeed.Value(); ok {
			return v.ShortfallDays, ""
		}
	}
	return 0, "projection unknown"
}

// ShadowRankOf ranks the candidate rows by projected shortfall per open
// action. openActions is each goal's open plan action count.
func ShadowRankOf(candidates []DevelopmentRow, projection ForwardProjection, openActions map[ConcernID]int) ShadowRank {
	var out ShadowRank
	for _, row := range candidates {
		if !shadowCandidate(row) {
			continue
		}
		domains, mapped := shadowDomains[row.Concern]
		if !mapped {
			continue
		}
		var d ShadowDomain
		var days float64
		why := "projection unknown"
		for _, cand := range domains {
			dd, w := shadowShortfall(projection, cand)
			if w != "" {
				if why == "projection unknown" {
					why = w
				}
				continue
			}
			if why != "" || dd > days {
				d, days, why = cand, dd, ""
			}
		}
		if why == "" && openActions[row.Concern] <= 0 {
			why = "no open plan action"
		}
		if why != "" {
			out.Unranked = append(out.Unranked, ShadowUnranked{Concern: row.Concern, Reason: why})
			continue
		}
		n := openActions[row.Concern]
		out.Ranked = append(out.Ranked, ShadowEntry{Concern: row.Concern, Domain: d, ShortfallDays: days, OpenActions: n, Score: math.RoundToEven(days/float64(n)*1000) / 1000})
	}
	sort.Slice(out.Ranked, func(i, j int) bool {
		a, b := out.Ranked[i], out.Ranked[j]
		if a.Score != b.Score {
			return a.Score > b.Score
		}
		return a.Concern < b.Concern
	})
	acquirerFirst(out.Ranked)
	return out
}

// acquirerFirst lifts MaintainResource, which acquires the material every
// construction goal waits on, ahead of the first construction goal ranked
// before it: a goal whose prerequisite is unbuilt is never ordered ahead of
// it, however many open actions it spreads its shortfall over.
func acquirerFirst(ranked []ShadowEntry) {
	at := slices.IndexFunc(ranked, func(e ShadowEntry) bool { return e.Concern == MaintainResource })
	if at < 0 {
		return
	}
	for i := 0; i < at; i++ {
		if ranked[i].Domain == ShadowConstruction {
			acquirer := ranked[at]
			copy(ranked[i+1:at+1], ranked[i:at])
			ranked[i] = acquirer
			return
		}
	}
}
