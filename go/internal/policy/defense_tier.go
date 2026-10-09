package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DefensePromotionFloor is the raid-point level below which defense is never
// promoted: the lowest armory threshold, so one or two early stragglers do not
// lift the wall over shelter and food (#2527).
const DefensePromotionFloor = 300.0

// DefensePromoted reports that projected raid points could overpower the
// colony: at least the floor and above the observed defense capacity. An
// unknown raid size or capacity is never a promotion.
func DefensePromoted(raidPoints, capacity domain.Fact[float64]) bool {
	raid, rk := raidPoints.Value()
	have, ck := capacity.Value()
	return rk && ck && raid >= DefensePromotionFloor && raid > have
}

// DefenseBuildTier is the construction tier of a defense build. Defense is Secure
// unless promoted to Survive; while a roamer is owned the core ring takes
// Comfort so its materials arrive before other Secure builds, and a promotion
// still outranks that.
func DefenseBuildTier(promoted, coreRing, roamerOwned bool) domain.ConstructionTier {
	switch {
	case promoted:
		return domain.TierSurvive
	case coreRing && roamerOwned:
		return domain.TierComfort
	default:
		return domain.TierSecure
	}
}

// DefenseRetiers selects the placed sites of the defense layout that a
// promotion must lift to Survive: a layout building whose blueprint or frame
// carries a known tier other than Survive. A site with no tier is ungated and
// stays as it is.
func DefenseRetiers(sites []ConstructionSite, layout []domain.Building) []ConstructionSite {
	type key struct {
		definition string
		cell       domain.Cell
	}
	wanted := map[key]bool{}
	for _, b := range layout {
		wanted[key{b.Definition(), b.Cell()}] = true
	}
	var out []ConstructionSite
	for _, s := range sites {
		tier, known := s.Tier.Value()
		if s.ID == "" || !known || tier == domain.TierSurvive || !wanted[key{s.Building.Definition(), s.Building.Cell()}] {
			continue
		}
		out = append(out, s)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].ID < out[j].ID })
	return out
}
