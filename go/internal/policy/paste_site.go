package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const SitePaste SiteKind = "nutrient-paste"

type PasteSiteRequest struct {
	Meals   MealTierRequest
	Benches domain.Fact[[]ProductionBench]
	Hopper  domain.Fact[Infrastructure]
	Size    domain.Fact[Bounds]
	Power   domain.Fact[PowerTopology]
}

// Paste shares site admission with growing infrastructure. The conservative
// clear apron leaves room for an adjacent hopper and the dispensing cell;
// native placement previews remain authoritative for both footprints.
func planPasteSite(r SiteTypeRequest) (SiteTypePlan, bool) {
	v := r.Paste
	review, err := ReviewMealTier(v.Meals, v.Benches)
	hopper, hk := v.Hopper.Value()
	size, sk := v.Size.Value()
	power, pk := v.Power.Value()
	if err != nil || review.Tier != MealPaste || !hk || !positive(hopper.Available) || !sk || size.Width <= 0 || size.Height <= 0 || size.Width > 6 || size.Height > 6 || !pk {
		return SiteTypePlan{}, false
	}
	cells := map[domain.Cell]bool{}
	listed := map[domain.Cell]bool{}
	rock := map[domain.Cell]bool{}
	for _, c := range r.Field.Site.Cells {
		zone, zk := c.Zone.Value()
		cells[c.Cell] = positive(c.Walkable) && !c.Occupied() && zk && !zone
		listed[c.Cell] = true
		rock[c.Cell] = rockCell(c)
	}
	for _, c := range r.Field.Site.Protected {
		cells[c] = false
		rock[c] = false
	}
	for _, c := range r.Field.Site.Cells {
		connected := false
		for _, b := range power.Buildings {
			net, nk := b.Network.Value()
			connected = connected || nk && net == review.PasteNetwork && positive(b.Connected) && distanceSquared(c.Cell, b.Cell) <= 36
		}
		if !connected {
			continue
		}
		// RimWorld's north-facing footprint is centred with the even-size
		// surplus on its positive edge. Hopper touches the west edge.
		h := domain.Cell{X: c.Cell.X - (size.Width-1)/2 - 1, Z: c.Cell.Z}
		// The dispenser's anchor and the hopper may stand on natural rock or
		// fogged mountain, which the rock step mines first; the rest of the
		// 9x9 must be open ground a pawn can work from.
		minable := func(p domain.Cell) bool { return rock[p] || !listed[p] && p == h }
		clear := true
		for x := int32(-4); x <= 4; x++ {
			for z := int32(-4); z <= 4; z++ {
				p := domain.Cell{X: c.Cell.X + x, Z: c.Cell.Z + z}
				clear = clear && (cells[p] || (p == c.Cell || p == h) && minable(p))
			}
		}
		if !clear || slices.Contains(r.Field.Site.Protected, h) {
			continue
		}
		infra, _ := v.Meals.Paste.Value()
		buildings := []SiteBuilding{{Definition: infra.Name, Cell: c.Cell, Rotation: domain.North}, {Definition: hopper.Name, Cell: h, Rotation: domain.North}}
		candidate := SiteTypeCandidate{Kind: SitePaste, Buildings: buildings, Cells: 2, Terms: []FarmSiteTerm{{Name: "power_w", Value: func() float64 { w, _ := infra.PowerW.Value(); return w }()}}}
		return SiteTypePlan{Kind: SitePaste, Buildings: buildings, Candidates: []SiteTypeCandidate{candidate}}, true
	}
	return SiteTypePlan{}, false
}
