package policy

import (
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// SafetyLayer is the overlay layer the safety assessment draws on (#824).
const SafetyLayer = "safety"

// ThreatReachCells is the drawn reach around a holding threat: the serve
// watch policy's hostile_within, the distance at which the native
// supervisor stops a running window for a hostile (DistantThreatCells).
const ThreatReachCells int32 = 20

// RaidEdgeCells is the native RaidArrivalState edge margin: a hostile lord
// whose first pawn spawns this close to the map edge arrived on the ground
// (#620).
const RaidEdgeCells int32 = 14

var (
	dangerHue = overlayHue{1, 0.15, 0.15}
	vetoHue   = overlayHue{1, 0.55, 0.1}
)

// SafetyOverlay draws what the bot considers dangerous (#824): a red fill
// and outline around every threat that holds the emergency (ThreatHolds),
// with the raid-arrival edge margin while a raid holds, and an orange
// outline over each stack the loot census vetoed as unsafe to haul. One
// label per connected area names its causes. Empty (the layer clears)
// when no threat holds and nothing is vetoed.
func SafetyOverlay(threats []EmergencyThreat, loot []LootItem, bounds Bounds) LayoutOverlay {
	var out LayoutOverlay
	cause := map[domain.Cell]string{}
	var seeds []domain.Cell
	raid := false
	for _, t := range threats {
		if !ThreatHolds(t) {
			continue
		}
		name := threatCause(t)
		raid = raid || name == "raid"
		cells := t.Cells
		if at, known := t.Position.Value(); known && !t.Building() {
			cells = []domain.Cell{at}
		}
		for _, at := range cells {
			if !inBounds(at, bounds) {
				continue
			}
			if _, seen := cause[at]; !seen {
				cause[at] = name
				seeds = append(seeds, at)
			}
		}
	}
	danger := map[domain.Cell]bool{}
	for _, at := range seeds {
		for z := at.Z - ThreatReachCells; z <= at.Z+ThreatReachCells; z++ {
			for x := at.X - ThreatReachCells; x <= at.X+ThreatReachCells; x++ {
				if c := (domain.Cell{X: x, Z: z}); inBounds(c, bounds) {
					danger[c] = true
				}
			}
		}
	}
	if len(danger) > 0 {
		runs := cellRuns(setCells(danger))
		out.Layers = append(out.Layers,
			OverlayLayer{Color: dangerHue.fill(), Style: OverlayFill, Label: "danger", Runs: runs},
			OverlayLayer{Color: dangerHue.outline(), Style: OverlayOutline, Label: "danger", Runs: runs})
		out.Labels = append(out.Labels, areaLabels(danger, seeds, cause)...)
	}
	if raid {
		if band := edgeBand(bounds, RaidEdgeCells); len(band) > 0 {
			out.Layers = append(out.Layers,
				OverlayLayer{Color: dangerHue.fill(), Style: OverlayFill, Label: "raid edge", Rects: band},
				OverlayLayer{Color: dangerHue.outline(), Style: OverlayOutline, Label: "raid edge", Rects: band})
			out.Labels = append(out.Labels, OverlayLabel{Text: "raid edge", Cell: domain.Cell{X: bounds.Width / 2, Z: RaidEdgeCells / 2}})
		}
	}
	vetoed := map[domain.Cell]bool{}
	var vetoSeeds []domain.Cell
	vetoCause := map[domain.Cell]string{}
	for _, row := range loot {
		at := row.Supply.Cell
		if !row.SafetyKnown || row.SafeToHaul || !inBounds(at, bounds) || vetoed[at] {
			continue
		}
		vetoed[at], vetoCause[at] = true, "unsafe route"
		vetoSeeds = append(vetoSeeds, at)
	}
	if len(vetoed) > 0 {
		out.Layers = append(out.Layers, OverlayLayer{Color: vetoHue.outline(), Style: OverlayOutline, Label: "unsafe route", Runs: cellRuns(setCells(vetoed))})
		out.Labels = append(out.Labels, areaLabels(vetoed, vetoSeeds, vetoCause)...)
	}
	return out
}

// threatCause names a holding threat for its area's label.
func threatCause(t EmergencyThreat) string {
	switch t.Kind {
	case HuntingPredator:
		return "predator"
	case HostileBuilding:
		return strings.ToLower(t.Definition)
	}
	if animal, known := t.Animal.Value(); known && animal {
		return "manhunter"
	}
	return "raid"
}

// areaLabels labels each 4-connected area of cells once, at its first
// seed, naming every cause seeded inside it.
func areaLabels(cells map[domain.Cell]bool, seeds []domain.Cell, cause map[domain.Cell]string) []OverlayLabel {
	area := map[domain.Cell]int{}
	var labels []OverlayLabel
	var causes []map[string]bool
	for _, seed := range seeds {
		if id, done := area[seed]; done {
			causes[id][cause[seed]] = true
			continue
		}
		id := len(labels)
		area[seed] = id
		for queue := []domain.Cell{seed}; len(queue) > 0; queue = queue[1:] {
			c := queue[0]
			for _, n := range []domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}} {
				if _, done := area[n]; cells[n] && !done {
					area[n] = id
					queue = append(queue, n)
				}
			}
		}
		labels = append(labels, OverlayLabel{Cell: seed})
		causes = append(causes, map[string]bool{cause[seed]: true})
	}
	for i := range labels {
		names := make([]string, 0, len(causes[i]))
		for name := range causes[i] {
			names = append(names, name)
		}
		sort.Strings(names)
		labels[i].Text = strings.Join(names, ", ")
	}
	return labels
}

// edgeBand is the map's outer margin cells deep as non-overlapping
// rectangles: bottom and top rows full width, then the two sides.
func edgeBand(b Bounds, margin int32) []Rectangle {
	if b.Width <= 0 || b.Height <= 0 || margin <= 0 {
		return nil
	}
	m := min(margin, b.Height/2, b.Width/2)
	if m <= 0 {
		return []Rectangle{{Width: b.Width, Height: b.Height}}
	}
	out := []Rectangle{{X: 0, Z: 0, Width: b.Width, Height: m}, {X: 0, Z: b.Height - m, Width: b.Width, Height: m}}
	if side := b.Height - 2*m; side > 0 {
		out = append(out, Rectangle{X: 0, Z: m, Width: m, Height: side}, Rectangle{X: b.Width - m, Z: m, Width: m, Height: side})
	}
	return out
}

func inBounds(c domain.Cell, b Bounds) bool {
	return c.X >= 0 && c.Z >= 0 && c.X < b.Width && c.Z < b.Height
}

func setCells(set map[domain.Cell]bool) []domain.Cell {
	out := make([]domain.Cell, 0, len(set))
	for c := range set {
		out = append(out, c)
	}
	return out
}
