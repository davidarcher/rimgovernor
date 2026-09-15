package policy

import (
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// FoodStorageStock pairs an observed FoodStock (reused as-is from
// food_forecast.go) with whether that stock already sits in an adequately
// enclosed/cold or covered stockpile. There is no native room-temperature
// read yet, so Stored is modelled as an already-computed boolean fact; the
// native read that produces it belongs to a later task, not this one.
type FoodStorageStock struct {
	Stock  FoodStock
	Stored domain.Fact[bool]
}

// FoodStorageObservation is the complete per-cycle census MaintainFoodStorage
// reviews. An unknown Stocks fact preserves the latch, the same posture
// MedicalReserveObservation's Items/Resources facts take in ReviewMedicalReserve.
type FoodStorageObservation struct {
	Stocks domain.Fact[[]FoodStorageStock]
}

// FoodStoragePolicy names the hysteresis band ReviewFoodStorage applies to the
// fraction of at-risk perishable nutrition that must be adequately stored,
// the same MinimumPerColonist/TargetPerColonist shape MedicalReservePolicy
// uses, plus a floor below which a tiny at-risk amount never activates the
// deficit (avoiding thrash over a single dropped ration).
type FoodStoragePolicy struct {
	MinimumStoredFraction, TargetStoredFraction float64
	AtRiskNutritionThreshold                    float64
}

func DefaultFoodStoragePolicy() FoodStoragePolicy {
	return FoodStoragePolicy{MinimumStoredFraction: 0.5, TargetStoredFraction: 0.9, AtRiskNutritionThreshold: 5}
}

func (p FoodStoragePolicy) valid() bool {
	return foodNumber(p.MinimumStoredFraction) && foodNumber(p.TargetStoredFraction) && foodNumber(p.AtRiskNutritionThreshold) &&
		p.MinimumStoredFraction < p.TargetStoredFraction && p.TargetStoredFraction <= 1
}

// FoodStorageReview is a latching deficit review, the same shape
// MedicalReserveReview uses: Active only flips off once storage recovers past
// the (higher) TargetStoredFraction, and an unavailable read preserves the
// prior Active value rather than guessing.
type FoodStorageReview struct {
	Active                                                              bool
	StoredNutrition, UnstoredNutrition, TotalNutrition, Target, Deficit domain.Fact[float64]
}

// ReviewFoodStorage tallies perishable, not-yet-rotted nutrition across every
// observed stock, splitting it into stored and unstored piles. It goes active
// when the unstored pile clears AtRiskNutritionThreshold and the stored share
// of total perishable nutrition falls under the current threshold (entry
// MinimumStoredFraction, or the higher TargetStoredFraction once already
// active), and clears once storage recovers past that same higher bar.
// Non-perishable and already-rotted stock never contributes: it is not at
// risk of being lost to inadequate storage. An unknown fact anywhere in the
// census preserves the latch untouched, matching ReviewMedicalReserve.
func ReviewFoodStorage(v FoodStorageObservation, active bool, p FoodStoragePolicy) (FoodStorageReview, error) {
	r := FoodStorageReview{Active: active}
	invalid := errors.New("invalid food storage facts or thresholds")
	if !p.valid() {
		return r, invalid
	}
	stocks, known := v.Stocks.Value()
	if !known {
		return r, nil
	}
	if len(stocks) > 4096 {
		return r, invalid
	}
	seen := map[string]bool{}
	var stored, unstored, total float64
	for _, entry := range stocks {
		s := entry.Stock
		if !foodID(s.ID) || seen[s.ID] {
			return r, invalid
		}
		seen[s.ID] = true
		nutrition, nk := s.Nutrition.Value()
		if nk && !foodNumber(nutrition) {
			return r, invalid
		}
		if ticks, tk := s.RotTicks.Value(); tk && ticks < 0 {
			return r, invalid
		}
		perishable, pk := s.Perishable.Value()
		if !nk || !pk || !perishable {
			continue
		}
		ticks, tk := s.RotTicks.Value()
		if !tk || ticks <= 0 {
			continue
		}
		storedFact, sk := entry.Stored.Value()
		if !sk {
			return r, nil
		}
		if total > math.MaxFloat64-nutrition {
			return r, invalid
		}
		total += nutrition
		if storedFact {
			stored += nutrition
		} else {
			unstored += nutrition
		}
	}
	if !foodNumber(stored) || !foodNumber(unstored) || !foodNumber(total) {
		return r, invalid
	}
	r.StoredNutrition, r.UnstoredNutrition, r.TotalNutrition = domain.Known(stored), domain.Known(unstored), domain.Known(total)
	if total == 0 {
		r.Active = false
		r.Target, r.Deficit = domain.Known(0.0), domain.Known(0.0)
		return r, nil
	}
	fraction := p.MinimumStoredFraction
	if active {
		fraction = p.TargetStoredFraction
	}
	threshold := fraction * total
	r.Active = unstored >= p.AtRiskNutritionThreshold && stored < threshold
	r.Target = domain.Known(threshold)
	deficit := 0.0
	if r.Active {
		deficit = math.Min(unstored, math.Max(0, threshold-stored))
	}
	r.Deficit = domain.Known(deficit)
	return r, nil
}

type FoodStorageMethodKind string

const (
	FoodStorageUnknown   FoodStorageMethodKind = "unknown"
	FoodStorageRecovered FoodStorageMethodKind = "recovered"
	FoodStorageRelocate  FoodStorageMethodKind = "relocate"
	FoodStorageProduce   FoodStorageMethodKind = "produce"
	FoodStorageBlocked   FoodStorageMethodKind = "no_eligible_method"
)

// FoodStorageSite is a candidate covered/enclosed stockpile SelectFoodStorageMethod
// may relocate at-risk stock into. Capacity is the site's remaining
// nutrition-equivalent room; it is a plain observed fact, not a reservation.
type FoodStorageSite struct {
	ID       string
	Capacity domain.Fact[int64]
}

// FoodStorageMethod is the tri-state (plus Recovered/Blocked) outcome
// SelectFoodStorageMethod proposes: relocate at-risk stock into an existing
// covered/enclosed site, or -- when no site has room -- fall back to a
// StockTarget-style production bill request for more preserved/non-perishable
// food (Resource/Target only; bench and recipe selection is the routine
// planner's job, the same deferral GearProduce/MaintainResource already use).
type FoodStorageMethod struct {
	Kind     FoodStorageMethodKind
	ID       domain.MethodID
	Site     string
	Amount   float64
	Resource Resource
	Target   int64
}

// FoodStoragePlanningRequest carries everything SelectFoodStorageMethod needs:
// the latched Review, the observed candidate Sites census, the one Resource
// name to fall back to producing (an operator-declared preserved/non-perishable
// food definition, the same config-only posture RoutinePolicy.ResourceTargets
// uses), and Seen method IDs already dispatched this cycle so a resolved
// relocation or bill is never proposed twice.
type FoodStoragePlanningRequest struct {
	Review   FoodStorageReview
	Sites    domain.Fact[[]FoodStorageSite]
	Resource Resource
	Seen     []domain.MethodID
}

func foodStorageMethodID(kind string, value any) domain.MethodID {
	data, _ := json.Marshal(value)
	sum := sha256.Sum256(append([]byte(kind+":"), data...))
	return domain.MethodID(fmt.Sprintf("food-storage-%s-%x", kind, sum[:16]))
}

// SelectFoodStorageMethod issues no game orders and reserves nothing; the
// shared method admission must recheck site capacity and production costs
// against concurrent plans before committing. With the deficit active, the
// lowest-ID candidate site (by deterministic sort, matching SelectMedicineMethod's
// bench ordering) with known spare capacity wins a Relocate; a duplicate
// relocation already in Seen is skipped rather than repeated. Only once every
// site is unusable or exhausted does it fall back to Produce.
func SelectFoodStorageMethod(r FoodStoragePlanningRequest) (FoodStorageMethod, error) {
	if !r.Review.Active {
		return FoodStorageMethod{Kind: FoodStorageRecovered}, nil
	}
	deficit, known := r.Review.Deficit.Value()
	if !known {
		return FoodStorageMethod{Kind: FoodStorageUnknown}, nil
	}
	if deficit <= 0 {
		return FoodStorageMethod{Kind: FoodStorageRecovered}, nil
	}
	if !foodNumber(deficit) || deficit > 1e9 {
		return FoodStorageMethod{}, errors.New("invalid food storage deficit")
	}
	if len(r.Seen) > 4096 {
		return FoodStorageMethod{}, errors.New("food storage method history exceeds bound")
	}
	seen := map[domain.MethodID]bool{}
	for _, id := range r.Seen {
		if !foodID(string(id)) || seen[id] {
			return FoodStorageMethod{}, errors.New("invalid food storage method history")
		}
		seen[id] = true
	}
	sites, known := r.Sites.Value()
	if !known {
		return FoodStorageMethod{Kind: FoodStorageUnknown}, nil
	}
	if len(sites) > 4096 {
		return FoodStorageMethod{}, errors.New("too many food storage sites")
	}
	candidates := append([]FoodStorageSite(nil), sites...)
	sort.Slice(candidates, func(i, j int) bool { return candidates[i].ID < candidates[j].ID })
	seenSite := map[string]bool{}
	for _, site := range candidates {
		if !foodID(site.ID) || seenSite[site.ID] {
			return FoodStorageMethod{}, errors.New("invalid or duplicate food storage site")
		}
		seenSite[site.ID] = true
	}
	sawUnknownCapacity := false
	for _, site := range candidates {
		capacity, ck := site.Capacity.Value()
		if !ck {
			sawUnknownCapacity = true
			continue
		}
		if capacity < 0 {
			return FoodStorageMethod{}, errors.New("invalid food storage site capacity")
		}
		if capacity <= 0 {
			continue
		}
		id := foodStorageMethodID("relocate", site.ID)
		if seen[id] {
			continue
		}
		amount := math.Min(deficit, float64(capacity))
		return FoodStorageMethod{Kind: FoodStorageRelocate, ID: id, Site: site.ID, Amount: amount}, nil
	}
	if sawUnknownCapacity {
		return FoodStorageMethod{Kind: FoodStorageUnknown}, nil
	}
	if !validResource(r.Resource) {
		return FoodStorageMethod{Kind: FoodStorageBlocked}, nil
	}
	target := int64(math.Ceil(deficit))
	if target < 1 {
		target = 1
	}
	if target > 10000 {
		return FoodStorageMethod{Kind: FoodStorageBlocked}, nil
	}
	produceID := foodStorageMethodID("produce", struct {
		Resource Resource
		Target   int64
	}{r.Resource, target})
	if seen[produceID] {
		return FoodStorageMethod{Kind: FoodStorageBlocked}, nil
	}
	return FoodStorageMethod{Kind: FoodStorageProduce, ID: produceID, Resource: r.Resource, Target: target}, nil
}
