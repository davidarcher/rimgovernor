package policy

import (
	"errors"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type AcquisitionKind string

const (
	AcquisitionLoot    AcquisitionKind = "loot"
	AcquisitionSalvage AcquisitionKind = "salvage"
	AcquisitionMining  AcquisitionKind = "mining"
)

// AcquisitionYield is an estimated output, not credited inventory. Headroom is
// known destination capacity accepting this exact output, in item units.
type AcquisitionYield struct {
	ResourceQuantity
	UnitValue float64
	Headroom  domain.Fact[int64]
}

// AcquisitionCandidate is shared by loot, salvage and mining. Callers provide
// eligible candidates after reach/safety checks; ranking never authorizes an
// order. PathDistance is native path length, Labor is estimated work ticks.
// UnitsPerTrip is the observed carrying capacity for these outputs. Hauling
// can be omitted only for output consumed at the worksite.
type AcquisitionCandidate struct {
	ID           string
	Kind         AcquisitionKind
	Yields       []AcquisitionYield
	PathDistance domain.Fact[float64]
	Labor        domain.Fact[float64]
	NeedsHaul    bool
	UnitsPerTrip int64
}

type AcquisitionCompetition struct {
	// Zero means no competing urgent work. Routine deficits are not a veto.
	// An urgent priority above every matched demand holds this candidate.
	UrgentPriority int
}

type AcquisitionScore struct {
	ID     string
	Kind   AcquisitionKind
	Score  float64
	Wanted int64
	Trips  int64
	Value  float64
	Hold   string
}

func finiteAcquisitionCost(v float64) bool {
	return !math.IsNaN(v) && !math.IsInf(v, 0) && v >= 0 && v <= 1e12
}

// RankResourceCandidates returns positive-scoring alternatives, not a batch
// allocation: callers select bounded work and rebuild demand from fresh facts.
// Only demanded units fitting destination headroom earn value. Costs include
// the entire source's labor and (conservatively) one rounded trip count per
// output, even when several outputs could share a trip.
func RankResourceCandidates(demand domain.Fact[[]ResourceDemand], candidates []AcquisitionCandidate, competition AcquisitionCompetition) ([]AcquisitionScore, error) {
	rows, known, err := acquisitionDemand(demand, competition)
	if err != nil {
		return nil, err
	}
	if len(candidates) > 4096 {
		return nil, errors.New("acquisition candidates exceed bound")
	}
	seen := map[struct {
		kind AcquisitionKind
		id   string
	}]bool{}
	var ranked []AcquisitionScore
	for _, c := range candidates {
		identity := struct {
			kind AcquisitionKind
			id   string
		}{c.Kind, c.ID}
		if seen[identity] {
			return nil, errors.New("duplicate acquisition candidate")
		}
		seen[identity] = true
		s, err := scoreResourceCandidate(rows, known, c, competition)
		if err != nil {
			return nil, err
		}
		if s.Score > 0 {
			ranked = append(ranked, s)
		}
	}
	sort.Slice(ranked, func(i, j int) bool {
		a, b := ranked[i], ranked[j]
		if a.Score != b.Score {
			return a.Score > b.Score
		}
		if a.Kind != b.Kind {
			return a.Kind < b.Kind
		}
		return a.ID < b.ID
	})
	return ranked, nil
}

// ScoreResourceCandidate exposes hold reasons for selection diagnostics.
func ScoreResourceCandidate(demand domain.Fact[[]ResourceDemand], candidate AcquisitionCandidate, competition AcquisitionCompetition) (AcquisitionScore, error) {
	rows, known, err := acquisitionDemand(demand, competition)
	if err != nil {
		return AcquisitionScore{}, err
	}
	return scoreResourceCandidate(rows, known, candidate, competition)
}

func scoreResourceCandidate(demand []ResourceDemand, known bool, c AcquisitionCandidate, competition AcquisitionCompetition) (AcquisitionScore, error) {
	s := AcquisitionScore{ID: c.ID, Kind: c.Kind}
	if !validResource(Resource(c.ID)) || len(c.Yields) > 4096 {
		return s, errors.New("invalid acquisition candidate")
	}
	switch c.Kind {
	case AcquisitionLoot, AcquisitionSalvage, AcquisitionMining, AcquisitionProduce, AcquisitionDeepDrill, AcquisitionChop, AcquisitionHarvest, AcquisitionHunt, AcquisitionTrade:
	default:
		return s, errors.New("invalid acquisition kind")
	}
	distance, pathKnown := c.PathDistance.Value()
	labor, laborKnown := c.Labor.Value()
	if pathKnown && !finiteAcquisitionCost(distance) || laborKnown && !finiteAcquisitionCost(labor) || !validAcquisitionCount(c.UnitsPerTrip) || c.NeedsHaul && c.UnitsPerTrip == 0 {
		return s, errors.New("invalid acquisition cost")
	}
	yields := append([]AcquisitionYield(nil), c.Yields...)
	sort.Slice(yields, func(i, j int) bool { return acquisitionKeyLess(yields[i].Key, yields[j].Key) })
	for i, y := range yields {
		headroom, storageKnown := y.Headroom.Value()
		if !validAcquisitionKey(y.Key) || !validAcquisitionCount(y.Count) || !finiteAcquisitionCost(y.UnitValue) || storageKnown && !validAcquisitionCount(headroom) || i > 0 && yields[i-1].Key == y.Key {
			return s, errors.New("invalid or duplicate acquisition yield")
		}
	}
	if !known || !pathKnown || !laborKnown {
		s.Hold = "unknown_demand_or_cost"
		return s, nil
	}
	remaining := make([]int64, len(demand))
	for i, d := range demand {
		remaining[i] = d.Count
	}
	priority := 0
	matched := false
	for _, y := range yields {
		capacity := y.Count
		if c.NeedsHaul {
			headroom, storageKnown := y.Headroom.Value()
			if !storageKnown {
				headroom = 0
			}
			capacity = min(capacity, headroom)
		}
		used := int64(0)
		for i, d := range demand {
			if d.Key.Def != y.Key.Def || d.Key.Stuff != "" && d.Key.Stuff != y.Key.Stuff {
				continue
			}
			if remaining[i] > 0 && y.Count > 0 {
				matched = true
			}
			count := min(capacity-used, remaining[i])
			if count == 0 {
				continue
			}
			remaining[i] -= count
			used += count
			priority = max(priority, d.Priority)
			s.Value += float64(count) * float64(d.Priority) * (1 + y.UnitValue)
		}
		s.Wanted += used
		if c.NeedsHaul && used > 0 {
			s.Trips += 1 + (used-1)/c.UnitsPerTrip
		}
	}
	if s.Wanted == 0 {
		s.Hold = "no_demand"
		if matched {
			s.Hold = "no_storage_headroom"
		}
		return s, nil
	}
	if competition.UrgentPriority > priority {
		s.Hold = "competing_urgent_work"
		return s, nil
	}
	// A bounded benefit/cost ratio keeps every unmet shortage eligible, however
	// distant, while otherwise equivalent nearby/low-labor sources rank first.
	s.Score = s.Value / (1 + labor + distance*(1+2*float64(s.Trips)) + float64(s.Trips))
	return s, nil
}

func acquisitionDemand(demand domain.Fact[[]ResourceDemand], competition AcquisitionCompetition) ([]ResourceDemand, bool, error) {
	rows, known := demand.Value()
	if len(rows) > 4096 || competition.UrgentPriority < 0 || competition.UrgentPriority > 100 {
		return nil, known, errors.New("invalid acquisition ranking input")
	}
	rows = append([]ResourceDemand(nil), rows...)
	sort.Slice(rows, func(i, j int) bool { return acquisitionKeyLess(rows[i].Key, rows[j].Key) })
	for i, d := range rows {
		if !validDemand(d) || (i > 0 && rows[i-1].Key == d.Key) {
			return nil, known, errors.New("invalid or duplicate acquisition demand")
		}
	}
	return rows, known, nil
}
