package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// SpotLayer is the overlay layer the outdoor stand-in work spots draw on.
const SpotLayer = "spots"

// spotLabels names the stand-ins the layout plan does not hold: the butcher
// spot and the crafting spot are placed by a search near the core, not
// planned ahead, so they are drawn from what stands or is blueprinted.
var spotLabels = map[string]overlayStyle{
	"ButcherSpot":  {planBrown, "butcher spot"},
	"TableButcher": {planBrown, "butcher table"},
	"CraftingSpot": {planTan, "crafting spot"},
}

// SpotOverlay outlines and labels every standing building and every
// blueprint of the stand-in work spots. Empty (the layer clears) when none
// stands or is blueprinted.
func SpotOverlay(construction CurrentConstruction, bounds Bounds) LayoutOverlay {
	var out LayoutOverlay
	draw := func(style overlayStyle, text string, cells []domain.Cell) {
		if len(cells) == 0 {
			return
		}
		var kept []domain.Cell
		for _, c := range cells {
			if inBounds(c, bounds) {
				kept = append(kept, c)
			}
		}
		if len(kept) == 0 {
			return
		}
		runs := cellRuns(kept)
		out.Layers = append(out.Layers,
			OverlayLayer{Color: style.hue.fill(), Style: OverlayFill, Label: text, Runs: runs},
			OverlayLayer{Color: style.hue.outline(), Style: OverlayOutline, Label: text, Runs: runs})
		out.Labels = append(out.Labels, OverlayLabel{Text: text, Cell: kept[0]})
	}
	for _, b := range construction.Buildings {
		if style, ok := spotLabels[b.Building.Definition()]; ok {
			cells := b.Cells
			if len(cells) == 0 {
				cells = []domain.Cell{b.Building.Cell()}
			}
			draw(style, style.label, cells)
		}
	}
	for _, s := range construction.Sites {
		if style, ok := spotLabels[s.Building.Definition()]; ok {
			draw(style, style.label+" (planned)", []domain.Cell{s.Building.Cell()})
		}
	}
	return out
}
