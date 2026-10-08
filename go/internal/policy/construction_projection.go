package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ConstructionClass is a build material pool the projector tracks (#2365).
type ConstructionClass string

const (
	ConstructionWood  ConstructionClass = "wood"
	ConstructionStone ConstructionClass = "stone"
	ConstructionSteel ConstructionClass = "steel"
)

// ConstructionInputs are what standing and admitted construction still needs
// and the stock it is measured against. Deficit and Stock are the facts
// MaintainResource's floors already read; Dependencies carry the previewed
// costs of admitted shelter methods (ShortfallDependency).
type ConstructionInputs struct {
	Deficit      domain.Fact[map[Resource]int64]
	Dependencies []DevelopmentDependency
	Stock        domain.Fact[[]Amount]
	Items        ItemFacts
}

// ConstructionClassProjection is one material pool: Need is the larger of
// the standing deficit and the admitted methods' open costs (the two overlap,
// as in ConstructionResourceNeeds), Stock the census, ShortfallDays the share
// of the horizon the missing units leave unsupplied.
type ConstructionClassProjection struct {
	Class          ConstructionClass
	Need, Stock    int64
	ShortfallUnits int64
	ShortfallDays  float64
}

type ConstructionProjection struct {
	Classes       []ConstructionClassProjection
	ShortfallDays float64 // the largest per-class shortfall
}

func (i ConstructionInputs) classOf(r Resource) (ConstructionClass, bool) {
	switch {
	case r == "WoodLog":
		return ConstructionWood, true
	case r == "Steel":
		return ConstructionSteel, true
	case i.Items.IsStoneBlocks(r):
		return ConstructionStone, true
	}
	return "", false
}

// projectConstruction compares construction material need with stock.
// Construction is a one-shot demand with no native burn rate, so units missing
// now are projected as the matching share of the horizon unsupplied (all of it
// when nothing is in stock). An unknown deficit or stock census is an unknown
// projection, never zero.
func projectConstruction(in ConstructionInputs) domain.Fact[ConstructionProjection] {
	owed, ok := in.Deficit.Value()
	rows, known := in.Stock.Value()
	if !ok || !known {
		return domain.Unknown[ConstructionProjection]()
	}
	need := map[Resource]int64{}
	for r, n := range owed {
		need[r] = n
	}
	for r, d := range dependencyDemands(in.Dependencies, MaintainResource) {
		need[r] = max(need[r], d.need)
	}
	needs, stock := map[ConstructionClass]int64{}, map[ConstructionClass]int64{}
	for r, n := range need {
		if c, ok := in.classOf(r); ok && n > 0 {
			needs[c] += n
		}
	}
	for _, row := range rows {
		if c, ok := in.classOf(row.Resource); ok {
			stock[c] += row.Count
		}
	}
	out := ConstructionProjection{}
	for c, n := range needs {
		short := max(0, n-stock[c])
		row := ConstructionClassProjection{Class: c, Need: n, Stock: stock[c], ShortfallUnits: short, ShortfallDays: ProjectionHorizonDays * float64(short) / float64(n)}
		out.ShortfallDays = max(out.ShortfallDays, row.ShortfallDays)
		out.Classes = append(out.Classes, row)
	}
	sort.Slice(out.Classes, func(i, j int) bool { return out.Classes[i].Class < out.Classes[j].Class })
	return domain.Known(out)
}
