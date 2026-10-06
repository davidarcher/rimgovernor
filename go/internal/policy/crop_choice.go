package policy

import (
	"errors"
	"fmt"
	"math"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ErrOutdoorsDarkUnknown refuses a decision that needs the biome's darkness
// when it was not read (#1712): lit is never assumed.
var ErrOutdoorsDarkUnknown = errors.New("cannot tell whether the biome keeps the sky dark: the colony frame carried no biome or no definition catalog was loaded")

type CropClimate struct {
	Sowing        domain.Fact[bool]
	DaysRemaining domain.Fact[float64]
	// OutdoorsDark is the biome's permanent darkness (#1712): the sky never
	// lights the ground, so no crop that needs light grows outdoors whatever
	// the season says. Unknown refuses: nothing is sown outdoors on a guess.
	OutdoorsDark domain.Fact[bool]
}

// SowingOutdoors is whether a crop that needs light can be sown outdoors
// now: the native season read, false in a permanently dark biome, unknown
// while the darkness is unknown.
func (c CropClimate) SowingOutdoors() domain.Fact[bool] {
	dark, known := c.OutdoorsDark.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	if dark {
		return domain.Known(false)
	}
	return c.Sowing
}

type CropChoice struct {
	// Harvests is the resource one harvest yields and UnitsPerCell the units
	// per cell per harvest (#2282); unknown stays unknown, never zero.
	// SowMinSkill is the sowing skill floor; HarvestDestroys is true when a
	// harvest removes the plant (a tree is felled), so the cell is empty after.
	Harvests                                                               domain.Fact[Resource]
	UnitsPerCell                                                           domain.Fact[float64]
	SowMinSkill                                                            domain.Fact[int32]
	HarvestDestroys                                                        domain.Fact[bool]
	Name                                                                   string
	Available, Edible                                                      domain.Fact[bool]
	GrowDays, FertilityMin, FertilitySensitivity, HarvestNutrition, Demand domain.Fact[float64]
	// SowTags are the native sow tags ("Ground", "Hydroponic"); MinGlow is the
	// native minimum light for growth, zero for cave crops.
	SowTags                                                         domain.Fact[[]string]
	MinGlow                                                         domain.Fact[float64]
	HarvestWork                                                     domain.Fact[float64]
	RawPreferred, DietAllowed, RequiresPollution, RequiresCleanSoil domain.Fact[bool]
	// RotDays and Perishable are the harvested item's rot facts; RotDays counts
	// only for a perishable item.
	RotDays    domain.Fact[float64]
	Perishable domain.Fact[bool]
}

// FieldRequest is one expansion decision: which edible crop to sow, and where,
// so that observed coverage reaches the reserve target.
type FieldRequest struct {
	Choices        []CropChoice
	Climate        CropClimate
	Runway         domain.Fact[float64]
	Colonists      domain.Fact[int64]
	Growers, Cooks domain.Fact[int]
	Calendar       domain.Fact[Calendar]
	Conditions     domain.Fact[[]DisasterCondition]
	// ReserveDays is the stored-food buffer FieldTarget budgets beyond one
	// cycle, spread over the harvests left in the season.
	ReserveDays float64
	// Coverage is the fraction of the target already growing (FieldCoverage).
	Coverage domain.Fact[float64]
	Site     FarmSiteRequest
}

// FieldCandidate is one crop's plan over the soil it would actually use.
// Score is the plan's net nutrition per day divided by the cells the crop
// needs, so a crop that cannot fill its own target is penalized.
type FieldCandidate struct {
	Crop   CropChoice
	Needed int
	Sites  FarmSitePlan
	Score  float64
	Urgent bool
	Reason string
	Terms  []FarmSiteTerm
}

type FieldPlan struct {
	Crop       CropChoice
	Needed     int
	Sites      FarmSitePlan
	Urgent     bool
	Candidates []FieldCandidate
}

func (p FieldPlan) Explain() string {
	var b strings.Builder
	fmt.Fprintf(&b, "%s needed=%d urgent=%t", p.Crop.Name, p.Needed, p.Urgent)
	for _, c := range p.Candidates {
		fmt.Fprintf(&b, "\n %s needed=%d cells=%d score=%.4f %s", c.Crop.Name, c.Needed, c.Sites.Cells, c.Score, c.Reason)
	}
	return b.String()
}

// PlanField chooses the crop and patches jointly. Every available edible crop
// with complete native facts is a candidate; its patches come from
// sitePick over unroofed free soil (the plan field blocks when set) at
// the crop own fertility floor, so a fertility-tolerant
// crop wins on poor soil where a richer crop would find no land. A remaining
// season shorter than 2.5 grow cycles excludes a crop; an unknown remaining
// season while sowing is possible is treated as short, so the fastest crop
// that can plant is chosen rather than nothing. A stored-food runway shorter
// than 2.5 cycles of the fastest crop is urgent and also prefers the fastest
// crop. Existing zones are never re-cropped: the plan only adds patches.
func PlanField(r FieldRequest) (FieldPlan, bool) {
	sowing, sk := r.Climate.SowingOutdoors().Value()
	if !sk || !sowing {
		return FieldPlan{}, false
	}
	return planField(r, false)
}

type viableCrop struct {
	crop   CropChoice
	days   float64
	needed int
	// units is the resource units one cell yields per harvest when the crop is
	// planned for a resource deficit (#2283); zero plans it for nutrition.
	units float64
}

// viableCrops screens the request's crops. With indoor set the remaining
// outdoor season is ignored: a roofed, lit and heated site grows all year.
func viableCrops(r FieldRequest, indoor bool) (viables []viableCrop, excluded []FieldCandidate, ok bool) {
	season, seasonKnown := r.Climate.DaysRemaining.Value()
	fraction, fk := r.Coverage.Value()
	if !fk || !foodNumber(fraction) || !indoor && seasonKnown && !fieldPositive(season) {
		return nil, nil, false
	}
	seen := map[string]bool{}
	for _, crop := range r.Choices {
		if seen[crop.Name] {
			return nil, nil, false
		}
		seen[crop.Name] = true
	}
	exclude := func(crop CropChoice, reason string) {
		excluded = append(excluded, FieldCandidate{Crop: crop, Reason: reason})
	}
	for _, crop := range r.Choices {
		if allowed, known := crop.DietAllowed.Value(); known && !allowed || GrowsInDark(crop) && !known {
			exclude(crop, "crop diet unavailable or penalised")
			continue
		}
		available, ak := crop.Available.Value()
		edible, ek := crop.Edible.Value()
		days, gk := crop.GrowDays.Value()
		switch {
		case !ak || !ek || !gk || !fieldPositive(days):
			exclude(crop, "incomplete crop facts")
			continue
		case !available || !edible:
			exclude(crop, "not an available edible crop")
			continue
		case !indoor && GrowsInDark(crop):
			exclude(crop, "crop needs a dark room")
			continue
		case !indoor && seasonKnown && days*2.5 > season:
			exclude(crop, "season too short")
			continue
		}
		left := r.Climate.DaysRemaining
		if indoor {
			left = domain.Known(fieldYearDays)
		}
		count, known := FieldTarget(r.Colonists, crop, r.ReserveDays, left).Value()
		if !known {
			exclude(crop, "field target unknown")
			continue
		}
		needed := int(math.Ceil(float64(count)*(1-fraction) - 1e-9))
		if needed <= 0 {
			return nil, nil, false
		}
		viables = append(viables, viableCrop{crop: crop, days: days, needed: needed})
	}
	return viables, excluded, true
}

// fieldUrgency is true when the stored-food runway is shorter than 2.5 cycles
// of the fastest viable crop, or (outdoors) when the remaining season is
// unknown while sowing is possible.
func fieldUrgency(r FieldRequest, viables []viableCrop, indoor bool) bool {
	if len(viables) == 0 {
		return false
	}
	fastest := viables[0].days
	for _, v := range viables {
		fastest = min(fastest, v.days)
	}
	_, seasonKnown := r.Climate.DaysRemaining.Value()
	foodDays, runwayKnown := r.Runway.Value()
	return !indoor && !seasonKnown || runwayKnown && foodNumber(foodDays) && foodDays < fastest*2.5
}

func planField(r FieldRequest, indoor bool) (FieldPlan, bool) {
	viables, excluded, ok := viableCrops(r, indoor)
	if !ok {
		return FieldPlan{}, false
	}
	plan := FieldPlan{Candidates: excluded}
	if len(viables) == 0 {
		return plan, false
	}
	urgent := fieldUrgency(r, viables, indoor)
	plan.Urgent = urgent
	for _, v := range viables {
		c := fieldCandidate(r, v, urgent)
		plan.Candidates = append(plan.Candidates, c)
	}
	return rankField(plan, urgent)
}

// fieldCandidate sites one viable crop on unroofed free soil (the plan field
// blocks when set) and scores it per needed cell.
func fieldCandidate(r FieldRequest, v viableCrop, urgent bool) FieldCandidate {
	site := r.Site
	sites := sitePick(site, v, func(s SiteCell) bool { return siteKnownFalse(s.Roofed) && (site.Fields == nil || site.Fields[s.Cell]) }, nil, false)
	c := FieldCandidate{Crop: v.crop, Needed: v.needed, Sites: sites, Urgent: urgent}
	if sites.Cells == 0 {
		c.Reason = "no plantable soil"
		return c
	}
	c.Terms = siteTerms(sites)
	if v.units == 0 {
		c.Terms = append(c.Terms, cropChoiceTerms(r, v.crop, sites.Cells)...)
	}
	total := 0.0
	for _, term := range c.Terms {
		total += term.Value
	}
	c.Score = total / float64(v.needed)
	c.Reason = fmt.Sprintf("net %.4f/day over %d patches", total, len(sites.Patches))
	return c
}

// rankField orders the plan's candidates and adopts the best plantable one.
func rankField(plan FieldPlan, urgent bool) (FieldPlan, bool) {
	sort.Slice(plan.Candidates, func(i, j int) bool {
		return fieldCandidateLess(plan.Candidates[i], plan.Candidates[j], urgent)
	})
	best := plan.Candidates[0]
	if best.Sites.Cells == 0 {
		return plan, false
	}
	plan.Crop, plan.Needed, plan.Sites = best.Crop, best.Needed, best.Sites
	return plan, true
}

// fieldCandidateLess orders plantable candidates first, then the fastest crop
// under urgency, then the highest score, then the fastest crop, then by name.
func fieldCandidateLess(a, b FieldCandidate, urgent bool) bool {
	if (a.Sites.Cells > 0) != (b.Sites.Cells > 0) {
		return a.Sites.Cells > 0
	}
	ad, _ := a.Crop.GrowDays.Value()
	bd, _ := b.Crop.GrowDays.Value()
	if urgent && ad != bd {
		return ad < bd
	}
	if a.Score != b.Score {
		return a.Score > b.Score
	}
	if ad != bd {
		return ad < bd
	}
	return a.Crop.Name < b.Crop.Name
}

func freeCropSoil(cell SiteCell, minimum float64, blocked map[domain.Cell]bool) (float64, bool) {
	walk, wk := cell.Walkable.Value()
	zone, zk := cell.Zone.Value()
	roof, rk := cell.Roofed.Value()
	soil, fk := cell.Fertility.Value()
	return soil, !blocked[cell.Cell] && wk && walk && !cell.Occupied() && zk && !zone && rk && !roof && fk && foodNumber(soil) && soil >= minimum
}

// FieldBlockOption is one viable crop for a new outdoor field block and the
// cells it still needs.
type FieldBlockOption struct {
	Crop   CropChoice
	Needed int
}

// FieldBlockNeighbourRadius is how near (cells) a growing zone must be to a
// new block for its crop to count as a neighbour.
const FieldBlockNeighbourRadius = 11

// BlockCropOrder orders the viable crops for a new field block (#1225):
// options keep their preference order, so urgency's fastest crop still
// leads the first block, but a crop growing in a neighbouring zone moves
// behind every other crop. A sole viable crop is still allowed.
func BlockCropOrder(options []FieldBlockOption, neighbours map[string]bool) []FieldBlockOption {
	out := make([]FieldBlockOption, 0, len(options))
	for _, o := range options {
		if !neighbours[o.Crop.Name] {
			out = append(out, o)
		}
	}
	for _, o := range options {
		if neighbours[o.Crop.Name] {
			out = append(out, o)
		}
	}
	return out
}

// fieldYearDays is a RimWorld year: an indoor field's reserve spreads over it.
const fieldYearDays float64 = domain.DaysPerYear

// FieldTarget is the cells of crop that feed the colony (#1252): each cell
// harvests yield every GrowDays, so steady state is demand*days/yield
// cells, and the reserve stock is spread over the harvests left in the
// season (one harvest when the season is unknown or shorter than a cycle).
func FieldTarget(colonists domain.Fact[int64], crop CropChoice, reserve float64, season domain.Fact[float64]) domain.Fact[int] {
	count, ck := colonists.Value()
	demand, dk := crop.Demand.Value()
	days, gk := crop.GrowDays.Value()
	yield, yk := crop.HarvestNutrition.Value()
	if !ck || count < 0 || !dk || !gk || !yk || !fieldPositive(demand) || !fieldPositive(days) || !fieldPositive(yield) || !fieldPositive(reserve) {
		return domain.Unknown[int]()
	}
	harvests := 1.0
	if left, ok := season.Value(); ok && fieldPositive(left) {
		harvests = math.Max(1, math.Floor(left/days))
	}
	target := math.Max(float64(count)*10, math.Ceil(demand*(days+reserve/harvests)/yield))
	if !fieldPositive(target) || target > 65536 {
		return domain.Unknown[int]()
	}
	return domain.Known(int(target))
}
