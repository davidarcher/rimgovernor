package policy

import (
	"fmt"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// StockLayer is the overlay layer the stockpile shortfall tint draws on
// (#825).
const StockLayer = "stock"

// stockFood is the food target's name: stored food in days against the
// seasonal food target, not a definition count.
const stockFood Resource = "food"

// StockZone is one stockpile zone for the stock overlay: every stockpile on
// the map (#719). Role and Filter are the colony's claim on it when it
// created the zone (Filter known then); FoodStorage is the zone census'
// human-food flag; Label is the native zone label.
type StockZone struct {
	ID, Role, Label string
	Filter          domain.Fact[domain.StockpileFilter]
	FoodStorage     bool
	Cells           []domain.Cell
}

// StockLevels are the targets a zone is tinted against: the review's
// effective resource targets with the census' stock of each, and stored
// food in days against the seasonal target.
type StockLevels struct {
	Targets        map[Resource]int64
	Stock          map[Resource]int64
	FoodDays       domain.Fact[float64]
	FoodTargetDays float64
}

// Shortfall bands: under stockShortFill of the target is short (red),
// under the target amber, at or over it met (green).
const stockShortFill = 0.5

// StockOverlay tints every stockpile zone by the fill of the worst-covered
// target it stores (red short, amber, green met) and labels it with that
// resource's have/target; a zone storing nothing with a target gets only a
// label of what it holds. A zone stores a target when its filter allows
// only that definition, when its role or food flag makes it a food store
// (food days), when its role is a medicine store (every Medicine* target),
// or when it is a general store (role general, or an everything or
// non-perishables filter: every non-food target).
func StockOverlay(zones []StockZone, levels StockLevels, bounds Bounds) LayoutOverlay {
	sorted := append([]StockZone(nil), zones...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i].ID < sorted[j].ID })
	bands := [3][]domain.Cell{}
	var out LayoutOverlay
	for _, z := range sorted {
		var cells []domain.Cell
		for _, c := range z.Cells {
			if inBounds(c, bounds) {
				cells = append(cells, c)
			}
		}
		if len(cells) == 0 {
			continue
		}
		at := labelCell(cells)
		resource, have, target, ok := worstStock(z, levels)
		if !ok {
			out.Labels = append(out.Labels, OverlayLabel{Text: z.holds(), Cell: at})
			continue
		}
		band := 2
		switch fill := have / target; {
		case fill < stockShortFill:
			band = 0
		case fill < 1:
			band = 1
		}
		bands[band] = append(bands[band], cells...)
		text := fmt.Sprintf("%s %d/%d", stockName(resource), int64(have), int64(target))
		if resource == stockFood {
			text = fmt.Sprintf("food %.1f/%.0f days", have, target)
		}
		out.Labels = append(out.Labels, OverlayLabel{Text: text, Cell: at})
	}
	for i, hue := range []overlayHue{planRed, planAmber, planGreen} {
		if len(bands[i]) > 0 {
			out.Layers = append(out.Layers, OverlayLayer{Color: hue.fill(), Style: OverlayFill, Label: []string{"short", "low", "met"}[i], Runs: cellRuns(bands[i])})
		}
	}
	return out
}

// worstStock is the zone's stored target with the lowest fill.
func worstStock(z StockZone, levels StockLevels) (Resource, float64, float64, bool) {
	var best Resource
	var have, target float64
	found := false
	consider := func(r Resource, h, t float64) {
		if t <= 0 {
			return
		}
		if !found || h/t < have/target || h/t == have/target && r < best {
			best, have, target, found = r, h, t, true
		}
	}
	food, general, defs := z.stores()
	if days, known := levels.FoodDays.Value(); food && known {
		consider(stockFood, days, levels.FoodTargetDays)
	}
	for r, t := range levels.Targets {
		if general || defs[r] || z.medicine() && strings.HasPrefix(string(r), "Medicine") {
			consider(r, float64(levels.Stock[r]), float64(t))
		}
	}
	return best, have, target, found
}

// stores reports whether the zone is a food store, a general store, and
// the definitions an allow-only filter names.
func (z StockZone) stores() (food, general bool, defs map[Resource]bool) {
	prefix, _, _ := strings.Cut(z.Role, ":")
	food = z.FoodStorage || prefix == "meals" || prefix == "rawfood" || prefix == "ingredients"
	general = z.Role == domain.GeneralRole
	filter, known := z.Filter.Value()
	if !known {
		return food, general, nil
	}
	switch filter.Base() {
	case domain.BaseFood, domain.BasePerishables:
		food = true
	case domain.BaseEverything, domain.BaseNonperishables, domain.BaseIndoorOnly:
		general = true
	}
	if names, ok := filter.AllowOnlyDefinitions(); ok {
		defs = map[Resource]bool{}
		for _, n := range names {
			defs[Resource(n)] = true
		}
	}
	return food, general, defs
}

func (z StockZone) medicine() bool {
	prefix, _, _ := strings.Cut(z.Role, ":")
	return prefix == "medicine"
}

// holds names what a zone without a target is for: its role, else its
// native label, ASCII only (#600).
func (z StockZone) holds() string {
	text := z.Role
	if text == "" {
		text = z.Label
	}
	if text == "" {
		text = "stockpile"
	}
	return strings.Map(func(r rune) rune {
		if r < 0x20 || r > 0x7e {
			return '?'
		}
		return r
	}, text)
}

// stockName is a target's label name: wood for WoodLog, else the definition.
func stockName(r Resource) string {
	if r == "WoodLog" {
		return "wood"
	}
	return string(r)
}

// labelCell is the zone cell nearest the zone's centroid.
func labelCell(cells []domain.Cell) domain.Cell {
	var sx, sz int64
	for _, c := range cells {
		sx, sz = sx+int64(c.X), sz+int64(c.Z)
	}
	n := int64(len(cells))
	cx, cz := int32(sx/n), int32(sz/n)
	best, dist := cells[0], int32(-1)
	for _, c := range cells {
		d := abs32(c.X-cx) + abs32(c.Z-cz)
		if dist < 0 || d < dist || d == dist && (c.Z < best.Z || c.Z == best.Z && c.X < best.X) {
			best, dist = c, d
		}
	}
	return best
}
