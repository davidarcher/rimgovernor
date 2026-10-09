package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// LayoutOverlay is a plan drawn as one native overlay layer: output
// only, replaced whole on every draw. Each planned room or module is
// outlined in its role's color with its role name as a text label; zones
// and hallways are filled.
type LayoutOverlay struct {
	Layers []OverlayLayer
	Labels []OverlayLabel
}

// OverlayLayer is one shape: a color, fill or outline, and its cells as
// rectangles and row runs. Label names it for tests and logs; the native
// draws only Labels.
type OverlayLayer struct {
	Color OverlayColor
	Style OverlayStyle
	Label string
	Rects []Rectangle
	Runs  []RowRun
}

// OverlayColor is straight RGBA in [0,1].
type OverlayColor struct{ R, G, B, A float32 }

// OverlayStyle is how a shape's cells draw.
type OverlayStyle uint8

const (
	// OverlayFill fills every cell.
	OverlayFill OverlayStyle = iota + 1
	// OverlayOutline draws only the boundary edges of the shape's cells.
	OverlayOutline
)

// OverlayLabel is text drawn over the map at a cell.
type OverlayLabel struct {
	Text string
	Cell domain.Cell
}

// overlayHue is a palette entry's RGB; fill and outline pick the alpha.
type overlayHue struct{ R, G, B float32 }

func (h overlayHue) fill() OverlayColor    { return OverlayColor{h.R, h.G, h.B, 0.3} }
func (h overlayHue) outline() OverlayColor { return OverlayColor{h.R, h.G, h.B, 0.85} }

// Overlay palette.
var (
	planGray       = overlayHue{0.6, 0.6, 0.6}
	planRed        = overlayHue{0.9, 0.2, 0.2}
	planYellow     = overlayHue{0.95, 0.85, 0.2}
	planGreen      = overlayHue{0.3, 0.8, 0.3}
	planCyan       = overlayHue{0.2, 0.85, 0.9}
	planBlue       = overlayHue{0.25, 0.4, 0.95}
	planPink       = overlayHue{0.95, 0.45, 0.75}
	planLightBlue  = overlayHue{0.55, 0.75, 1}
	planAmber      = overlayHue{1, 0.65, 0.1}
	planWhite      = overlayHue{0.95, 0.95, 0.95}
	planDarkPurple = overlayHue{0.4, 0.15, 0.5}
	planBrown      = overlayHue{0.55, 0.35, 0.2}
	planTan        = overlayHue{0.8, 0.7, 0.5}
	planViolet     = overlayHue{0.6, 0.4, 0.9}
)

// overlayStyle is a role's hue and label.
type overlayStyle struct {
	hue   overlayHue
	label string
}

// clip is r inside bounds, false when nothing is left.
func clip(r Rectangle, b Bounds) (Rectangle, bool) {
	minX, minZ := max(r.X, 0), max(r.Z, 0)
	maxX, maxZ := min(r.X+r.Width, b.Width), min(r.Z+r.Height, b.Height)
	if minX >= maxX || minZ >= maxZ {
		return Rectangle{}, false
	}
	return Rectangle{X: minX, Z: minZ, Width: maxX - minX, Height: maxZ - minZ}, true
}

// clipRun is r inside bounds, false when nothing is left.
func clipRun(r RowRun, b Bounds) (RowRun, bool) {
	c, ok := clip(Rectangle{X: r.X, Z: r.Z, Width: r.Length, Height: 1}, b)
	return RowRun{Z: c.Z, X: c.X, Length: c.Width}, ok
}

// cellRuns packs cells into row runs of consecutive x, z-major.
func cellRuns(cells []domain.Cell) []RowRun {
	rows := map[int32]map[int32]bool{}
	minZ, maxZ := int32(1<<30), int32(-1<<30)
	minX, maxX := int32(1<<30), int32(-1<<30)
	for _, c := range cells {
		if rows[c.Z] == nil {
			rows[c.Z] = map[int32]bool{}
		}
		rows[c.Z][c.X] = true
		minZ, maxZ, minX, maxX = min(minZ, c.Z), max(maxZ, c.Z), min(minX, c.X), max(maxX, c.X)
	}
	var out []RowRun
	for z := minZ; z <= maxZ; z++ {
		row := rows[z]
		for x := minX; x <= maxX; x++ {
			if !row[x] {
				continue
			}
			start := x
			for row[x+1] {
				x++
			}
			out = append(out, RowRun{Z: z, X: start, Length: x - start + 1})
		}
	}
	return out
}
