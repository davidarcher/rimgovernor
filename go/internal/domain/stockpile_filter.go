package domain

import (
	"encoding/json"
	"errors"
	"math"
	"sort"
)

// FilterBase is the native storage filter preset a StockpileFilter starts
// from (native FilterPreset) before its allow/disallow selectors apply.
type FilterBase string

const (
	BaseEverything     FilterBase = "everything"
	BaseNothing        FilterBase = "nothing"
	BaseFood           FilterBase = "food"
	BasePerishables    FilterBase = "perishables"
	BaseNonperishables FilterBase = "nonperishables"
	BaseOutdoorSafe    FilterBase = "outdoor_safe"
	BaseIndoorOnly     FilterBase = "indoor_only"
)

// SelectorKind names what a FilterSelector's name resolves against natively.
// The order is the canonical sort order of a selector list.
type SelectorKind string

const (
	ThingDefSelector      SelectorKind = "thing_def"
	CategoryDefSelector   SelectorKind = "category_def"
	SpecialFilterSelector SelectorKind = "special_filter_def"
)

func selectorRank(k SelectorKind) int {
	switch k {
	case ThingDefSelector:
		return 0
	case CategoryDefSelector:
		return 1
	case SpecialFilterSelector:
		return 2
	}
	return -1
}

// FilterSelector is one native FilterSelector: exactly one def name of one kind.
type FilterSelector struct {
	Kind SelectorKind
	Name string
}

func ThingDef(name string) FilterSelector      { return FilterSelector{ThingDefSelector, name} }
func CategoryDef(name string) FilterSelector   { return FilterSelector{CategoryDefSelector, name} }
func SpecialFilter(name string) FilterSelector { return FilterSelector{SpecialFilterSelector, name} }

// Quality is a native QualityCategory name.
type Quality string

var qualityOrder = []Quality{"Awful", "Poor", "Normal", "Good", "Excellent", "Masterwork", "Legendary"}

func qualityRank(q Quality) int {
	for i, v := range qualityOrder {
		if v == q {
			return i
		}
	}
	return -1
}

// StockpileFilter is the closed, comparable value of one storage filter
// (a stockpile zone's or a storage building's): a native base preset, then
// canonical (sorted, deduplicated, at most 32 each, disjoint) allow and
// disallow selectors, then optional hit-point fraction and quality ranges.
// It maps 1:1 onto native StockpileSettings.preset + FilterPatch.
type StockpileFilter struct {
	base             FilterBase
	allow, disallow  string
	hasHitPoints     bool
	hitMin, hitMax   float64
	hasQuality       bool
	qualMin, qualMax Quality
}

func canonicalSelectors(rows []FilterSelector) (string, []FilterSelector, error) {
	if len(rows) == 0 {
		return "", nil, nil
	}
	sorted := append([]FilterSelector(nil), rows...)
	sort.Slice(sorted, func(i, j int) bool {
		a, b := sorted[i], sorted[j]
		return selectorRank(a.Kind) < selectorRank(b.Kind) || selectorRank(a.Kind) == selectorRank(b.Kind) && a.Name < b.Name
	})
	for i, s := range sorted {
		if selectorRank(s.Kind) < 0 || !validID(s.Name) || i > 0 && sorted[i-1] == s {
			return "", nil, errors.New("invalid filter selector")
		}
	}
	data, _ := json.Marshal(sorted)
	return string(data), sorted, nil
}

// NewStockpileFilter canonicalizes a base preset plus allow/disallow
// selectors. A selector may not be both allowed and disallowed.
func NewStockpileFilter(base FilterBase, allow, disallow []FilterSelector) (StockpileFilter, error) {
	switch base {
	case BaseEverything, BaseNothing, BaseFood, BasePerishables, BaseNonperishables, BaseOutdoorSafe, BaseIndoorOnly:
	default:
		return StockpileFilter{}, errors.New("invalid filter base")
	}
	a, as, err := canonicalSelectors(allow)
	if err != nil {
		return StockpileFilter{}, err
	}
	d, ds, err := canonicalSelectors(disallow)
	if err != nil {
		return StockpileFilter{}, err
	}
	seen := map[FilterSelector]bool{}
	for _, s := range as {
		seen[s] = true
	}
	for _, s := range ds {
		if seen[s] {
			return StockpileFilter{}, errors.New("selector both allowed and disallowed")
		}
	}
	return StockpileFilter{base: base, allow: a, disallow: d}, nil
}

// WithHitPoints bounds the filter to a hit-point fraction range in [0,1].
func (f StockpileFilter) WithHitPoints(min, max float64) (StockpileFilter, error) {
	if math.IsNaN(min) || math.IsNaN(max) || min < 0 || max > 1 || min > max {
		return StockpileFilter{}, errors.New("invalid hit-point range")
	}
	f.hasHitPoints, f.hitMin, f.hitMax = true, min, max
	return f, nil
}

// WithQuality bounds the filter to a quality range (Awful..Legendary).
func (f StockpileFilter) WithQuality(min, max Quality) (StockpileFilter, error) {
	lo, hi := qualityRank(min), qualityRank(max)
	if lo < 0 || hi < 0 || lo > hi {
		return StockpileFilter{}, errors.New("invalid quality range")
	}
	f.hasQuality, f.qualMin, f.qualMax = true, min, max
	return f, nil
}

// ReconstructStockpileFilter rebuilds a canonical filter from a value of
// unknown provenance.
func ReconstructStockpileFilter(f StockpileFilter) (StockpileFilter, error) {
	out, err := NewStockpileFilter(f.base, f.Allow(), f.Disallow())
	if err == nil && f.hasHitPoints {
		out, err = out.WithHitPoints(f.hitMin, f.hitMax)
	}
	if err == nil && f.hasQuality {
		out, err = out.WithQuality(f.qualMin, f.qualMax)
	}
	return out, err
}

func (f StockpileFilter) Base() FilterBase { return f.base }
func decodeSelectors(s string) []FilterSelector {
	var rows []FilterSelector
	if s != "" {
		_ = json.Unmarshal([]byte(s), &rows)
	}
	return rows
}
func (f StockpileFilter) Allow() []FilterSelector    { return decodeSelectors(f.allow) }
func (f StockpileFilter) Disallow() []FilterSelector { return decodeSelectors(f.disallow) }
func (f StockpileFilter) HitPoints() (min, max float64, ok bool) {
	return f.hitMin, f.hitMax, f.hasHitPoints
}
func (f StockpileFilter) Quality() (min, max Quality, ok bool) {
	return f.qualMin, f.qualMax, f.hasQuality
}

// The planners' named filters. Their wire output is byte-identical to the
// presets they replace (bridge TestPresetFiltersWireUnchanged).

// FoodFilter is the native food preset, unpatched.
func FoodFilter() StockpileFilter { return StockpileFilter{base: BaseFood} }

// GeneralFilter is every non-perishable storable except chunks.
func GeneralFilter() StockpileFilter {
	f, _ := NewStockpileFilter(BaseNonperishables, nil, []FilterSelector{CategoryDef("Chunks")})
	return f
}

// YardFilter is the items safe outside, the native outdoor_safe preset.
func YardFilter() StockpileFilter { return StockpileFilter{base: BaseOutdoorSafe} }

// CorpseLarderFilter holds fresh animal and insect corpses only.
func CorpseLarderFilter() StockpileFilter {
	f, _ := NewStockpileFilter(BaseNothing, []FilterSelector{CategoryDef("CorpsesAnimal"), CategoryDef("CorpsesInsect"), SpecialFilter("AllowFresh")}, []FilterSelector{SpecialFilter("AllowRotten")})
	return f
}

// RawFoodFilter holds raw meat and raw plant food, never rotten: the
// freezer's cooking-ingredient stock (#722).
func RawFoodFilter() StockpileFilter {
	f, _ := NewStockpileFilter(BaseNothing, []FilterSelector{CategoryDef("MeatRaw"), CategoryDef("PlantFoodRaw")}, []FilterSelector{SpecialFilter("AllowRotten")})
	return f
}

// RawMeatFilter holds raw meat, never rotten: the freezer's meat shelf.
func RawMeatFilter() StockpileFilter {
	f, _ := NewStockpileFilter(BaseNothing, []FilterSelector{CategoryDef("MeatRaw")}, []FilterSelector{SpecialFilter("AllowRotten")})
	return f
}

// RawVegFilter holds raw plant food, never rotten: the freezer's produce shelf.
func RawVegFilter() StockpileFilter {
	f, _ := NewStockpileFilter(BaseNothing, []FilterSelector{CategoryDef("PlantFoodRaw")}, []FilterSelector{SpecialFilter("AllowRotten")})
	return f
}

// PerishablesFilter holds every food and fresh animal corpse that is not yet
// rotten: the freezer's catch-all under its dedicated shelves. Reserve food that
// never rots (survival meals, pemmican) belongs in general storage.
func PerishablesFilter() StockpileFilter {
	f, _ := NewStockpileFilter(BaseNothing, []FilterSelector{CategoryDef("Foods"), CategoryDef("CorpsesAnimal"), CategoryDef("CorpsesInsect")}, []FilterSelector{SpecialFilter("AllowRotten"), ThingDef("MealSurvivalPack"), ThingDef("Pemmican")})
	return f
}

// TombCorpsesFilter holds human corpses, fresh or not: the tomb room's stock
// until they are buried, entombed or butchered.
func TombCorpsesFilter() StockpileFilter {
	f, _ := NewStockpileFilter(BaseNothing, []FilterSelector{CategoryDef("CorpsesHumanlike")}, nil)
	return f
}

// MedicineFilter holds every medicine and nothing else: the hospital's store.
func MedicineFilter() StockpileFilter {
	f, _ := NewStockpileFilter(BaseNothing, []FilterSelector{CategoryDef("Medicine")}, nil)
	return f
}

// AllowOnlyFilter disallows everything except the named thing definitions
// (1..32 of them).
func AllowOnlyFilter(definitions []string) (StockpileFilter, error) {
	if len(definitions) == 0 {
		return StockpileFilter{}, errors.New("empty allow-only filter")
	}
	rows := make([]FilterSelector, len(definitions))
	for i, name := range definitions {
		rows[i] = ThingDef(name)
	}
	return NewStockpileFilter(BaseNothing, rows, nil)
}

// AllowOnlyDefinitions is an AllowOnlyFilter's thing definitions; ok is
// false for any other filter.
func (f StockpileFilter) AllowOnlyDefinitions() ([]string, bool) {
	if f.base != BaseNothing || len(f.Disallow()) != 0 || f.hasHitPoints || f.hasQuality {
		return nil, false
	}
	var names []string
	for _, s := range f.Allow() {
		if s.Kind != ThingDefSelector {
			return nil, false
		}
		names = append(names, s.Name)
	}
	return names, len(names) != 0
}

// stockpileFilterPayload is the filter's persisted and canonical JSON form.
type stockpileFilterPayload struct {
	Base      FilterBase
	Allow     []FilterSelector `json:",omitempty"`
	Disallow  []FilterSelector `json:",omitempty"`
	HitPoints *[2]float64      `json:",omitempty"`
	Quality   *[2]Quality      `json:",omitempty"`
}

func (f StockpileFilter) MarshalJSON() ([]byte, error) {
	p := stockpileFilterPayload{Base: f.base, Allow: f.Allow(), Disallow: f.Disallow()}
	if f.hasHitPoints {
		p.HitPoints = &[2]float64{f.hitMin, f.hitMax}
	}
	if f.hasQuality {
		p.Quality = &[2]Quality{f.qualMin, f.qualMax}
	}
	return json.Marshal(p)
}

func (f *StockpileFilter) UnmarshalJSON(data []byte) error {
	var p stockpileFilterPayload
	if err := json.Unmarshal(data, &p); err != nil {
		return err
	}
	out, err := NewStockpileFilter(p.Base, p.Allow, p.Disallow)
	if err == nil && p.HitPoints != nil {
		out, err = out.WithHitPoints(p.HitPoints[0], p.HitPoints[1])
	}
	if err == nil && p.Quality != nil {
		out, err = out.WithQuality(p.Quality[0], p.Quality[1])
	}
	if err != nil {
		return err
	}
	*f = out
	return nil
}
