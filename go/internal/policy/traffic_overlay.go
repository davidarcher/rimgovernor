package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// TrafficLayers is every counted layer in overlay order: the heat layers
// draw as "heat.<layer>".
var TrafficLayers = []TrafficLayer{TrafficColonist, TrafficCrossing, TrafficAnimal, TrafficVisitor, TrafficHostile}

var heatColor = map[TrafficLayer]OverlayColor{
	TrafficColonist: {R: 1, G: 0.55, B: 0.1},
	TrafficCrossing: {R: 0.55, G: 0.35, B: 0.15},
	TrafficAnimal:   {R: 0.3, G: 0.8, B: 0.3},
	TrafficVisitor:  {R: 0.3, G: 0.6, B: 1},
	TrafficHostile:  {R: 1, G: 0.15, B: 0.15},
}

// heatBands is the number of opacity steps a heat layer scales over.
const heatBands = 4

// TrafficOverlay draws one layer's busiest cells as a heat map scaled to
// its busiest cell: heatBands fills of rising opacity, row runs each.
func TrafficOverlay(cells []TrafficCell, layer TrafficLayer, bounds Bounds) LayoutOverlay {
	var peak uint32
	for _, c := range cells {
		if c.Layer == layer && c.Samples > peak {
			peak = c.Samples
		}
	}
	if peak == 0 {
		return LayoutOverlay{}
	}
	bands := make([][]domain.Cell, heatBands)
	for _, c := range cells {
		if c.Layer != layer || c.Samples == 0 || c.Cell.X < 0 || c.Cell.Z < 0 || c.Cell.X >= bounds.Width || c.Cell.Z >= bounds.Height {
			continue
		}
		b := int(uint64(c.Samples) * heatBands / (uint64(peak) + 1))
		bands[b] = append(bands[b], c.Cell)
	}
	var out LayoutOverlay
	for b, band := range bands {
		if len(band) == 0 {
			continue
		}
		color := heatColor[layer]
		color.A = 0.15 + 0.5*float32(b)/float32(heatBands-1)
		out.Layers = append(out.Layers, OverlayLayer{Color: color, Style: OverlayFill, Label: "heat " + string(layer), Runs: cellRuns(band)})
	}
	return out
}
