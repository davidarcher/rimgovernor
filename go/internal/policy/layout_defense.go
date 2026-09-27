package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The defense layout builds on the layout plan (#789, C5): the corridor,
// firing line and turrets stand in the plan's killbox, and the perimeter
// tier raises the plan's 3-thick wall with its gates in staged sections.

// TierPerimeterPrefix names the perimeter's sections: TierPerimeterPrefix
// plus a two-digit order, or TierGeothermal for the geyser's enclosure.
const (
	TierPerimeterPrefix = "perimeter-"
	TierGeothermal      = DefenseTierName(TierPerimeterPrefix + "geothermal")
	// perimeterSectionRun is a section's length along the wall: 20 cells of
	// a 3-thick wall is about 60 blocks.
	perimeterSectionRun int32 = 20
)

// IsPerimeterTier reports whether a tier is a perimeter section.
func IsPerimeterTier(name DefenseTierName) bool {
	return len(name) > len(TierPerimeterPrefix) && string(name[:len(TierPerimeterPrefix)]) == TierPerimeterPrefix
}

// The perimeter's footings on soft ground (#949): a plain bridge holds a
// wooden wall only; a heavy bridge, once researched, holds stone.
const (
	PerimeterBridge        = "Bridge"
	PerimeterHeavyBridge   = "HeavyBridge"
	PerimeterHeavyResearch = "HeavyBridges"
	PerimeterLightStuff    = "WoodLog"
)

// PerimeterSection is one staged piece of the wall.
type PerimeterSection struct {
	Name      DefenseTierName
	Buildings []domain.Building
}

// LayoutKillbox reads the plan's killbox opening: the anchor, the census
// region around it (inside bounds) and the home
// cell deep in the killbox. ok is false while the plan has no opening.
func LayoutKillbox(plan LayoutPlan, bounds Bounds) (k DefenseKillbox, region Rectangle, home domain.Cell, ok bool) {
	var killbox Rectangle
	var approaches []Rectangle
	for _, r := range plan.Reservations {
		switch r.Kind {
		case ReserveKillbox:
			killbox = r.Area
		case ReserveKillboxApproach:
			approaches = append(approaches, r.Area)
		case ReserveTurret:
			for x := r.Area.X; x < r.Area.X+r.Area.Width; x++ {
				for z := r.Area.Z; z < r.Area.Z+r.Area.Height; z++ {
					k.Turrets = append(k.Turrets, domain.Cell{X: x, Z: z})
				}
			}
		case ReservePerimeter:
			k.Walled = append(k.Walled, rectCells(r.Area)...)
		}
	}
	if killbox.Width == 0 {
		return DefenseKillbox{}, Rectangle{}, domain.Cell{}, false
	}
	// The straight approach leg is 3 wide and ends one cell outside the
	// opening's outer face; the killbox starts perimeterThick cells in.
	found := false
	for _, a := range approaches {
		for _, d := range directions {
			across, along := a.Width, a.Height
			if d.X != 0 {
				across, along = a.Height, a.Width
			}
			if across != 3 || along < 1 {
				continue
			}
			// The leg's end nearest the opening, centred across.
			end := domain.Cell{X: a.X + a.Width/2, Z: a.Z + a.Height/2}
			switch d {
			case domain.Cell{X: 1}:
				end.X = a.X + a.Width - 1
			case domain.Cell{X: -1}:
				end.X = a.X
			case domain.Cell{Z: 1}:
				end.Z = a.Z + a.Height - 1
			case domain.Cell{Z: -1}:
				end.Z = a.Z
			}
			entry := addCell(end, d)
			if contains(killbox, addCell(entry, scale(d, perimeterThick))) {
				k.Entry, k.Toward, k.Width, found = entry, rotationOf(d), 3, true
			}
		}
	}
	if !found {
		return DefenseKillbox{}, Rectangle{}, domain.Cell{}, false
	}
	home = domain.Cell{X: killbox.X + killbox.Width/2, Z: killbox.Z + killbox.Height/2}
	cover := killbox
	for _, a := range append(approaches, rectOf(k.Entry, k.Entry)) {
		cover = unionRect(cover, a)
	}
	region = clipRect(pad(cover, 3), Rectangle{Width: bounds.Width, Height: bounds.Height})
	if region.Width == 0 || !contains(region, home) {
		return DefenseKillbox{}, Rectangle{}, domain.Cell{}, false
	}
	// Only the wall near the opening matters to the funnel.
	var near []domain.Cell
	for _, c := range k.Walled {
		if contains(region, c) {
			near = append(near, c)
		}
	}
	k.Walled = near
	return k, region, home, true
}

// PerimeterSections cuts the plan's wall into sections of about 60 blocks,
// nearest the killbox first: walls, with a door on every gate cell (three
// in a row through the thickness). The geothermal site's shell, when the
// plan holds one, is its own section with a doorway of doors facing the
// core. A wall on water stands on bridge (#949), laid by a section of its
// own just before the wall's; a wall on light footing or on a plain Bridge
// is WoodLog. Other stuff is left empty; the planner fills in the stone at
// admission.
func PerimeterSections(plan LayoutPlan, wall, door, bridge string) ([]PerimeterSection, error) {
	gates, light, bridged := map[domain.Cell]bool{}, map[domain.Cell]bool{}, map[domain.Cell]bool{}
	var runs []Rectangle
	var killbox, geothermal Rectangle
	for _, r := range plan.Reservations {
		switch r.Kind {
		case ReserveGate:
			for _, c := range rectCells(r.Area) {
				gates[c] = true
			}
		case ReservePerimeter:
			runs = append(runs, r.Area)
		case ReservePerimeterLight:
			for _, c := range rectCells(r.Area) {
				light[c] = true
			}
		case ReserveBridge:
			for _, c := range rectCells(r.Area) {
				bridged[c] = true
			}
		case ReserveKillbox:
			killbox = r.Area
		case ReserveGeothermal:
			if geothermal.Width == 0 {
				geothermal = r.Area
			}
		}
	}
	type piece struct {
		area Rectangle
		dist int64
	}
	centre := domain.Cell{X: killbox.X + killbox.Width/2, Z: killbox.Z + killbox.Height/2}
	var pieces []piece
	var ring Rectangle
	for _, r := range runs {
		ring = unionRect(ring, r)
		horizontal := r.Width >= r.Height
		length := r.Height
		if horizontal {
			length = r.Width
		}
		for off := int32(0); off < length; off += perimeterSectionRun {
			n := min(perimeterSectionRun, length-off)
			a := Rectangle{X: r.X, Z: r.Z + off, Width: r.Width, Height: n}
			if horizontal {
				a = Rectangle{X: r.X + off, Z: r.Z, Width: n, Height: r.Height}
			}
			mid := domain.Cell{X: a.X + a.Width/2, Z: a.Z + a.Height/2}
			pieces = append(pieces, piece{a, squaredDistance(mid, centre)})
		}
	}
	sort.SliceStable(pieces, func(i, j int) bool {
		a, b := pieces[i], pieces[j]
		return a.dist < b.dist || a.dist == b.dist && (a.area.X < b.area.X || a.area.X == b.area.X && a.area.Z < b.area.Z)
	})
	var out []PerimeterSection
	next := func() PerimeterSection {
		return PerimeterSection{Name: DefenseTierName(fmt.Sprintf("%s%02d", TierPerimeterPrefix, len(out)))}
	}
	for _, p := range pieces {
		under := next()
		for _, c := range rectCells(p.area) {
			if !bridged[c] {
				continue
			}
			b, err := domain.NewBuilding(bridge, c, domain.North, "")
			if err != nil {
				return nil, err
			}
			under.Buildings = append(under.Buildings, b)
		}
		if len(under.Buildings) > 0 {
			out = append(out, under)
		}
		s := next()
		for _, c := range rectCells(p.area) {
			def, stuff := wall, ""
			if gates[c] {
				def = door
			}
			if light[c] || bridged[c] && bridge == PerimeterBridge {
				stuff = PerimeterLightStuff
			}
			b, err := domain.NewBuilding(def, c, domain.North, stuff)
			if err != nil {
				return nil, err
			}
			s.Buildings = append(s.Buildings, b)
		}
		out = append(out, s)
	}
	if geothermal.Width > 2*geothermalShell && geothermal.Height > 2*geothermalShell {
		gen := pad(geothermal, -geothermalShell)
		// The doorway crosses the shell on the side facing the ring's
		// centre (the core), mid-side.
		core := domain.Cell{X: ring.X + ring.Width/2, Z: ring.Z + ring.Height/2}
		mid := domain.Cell{X: gen.X + gen.Width/2, Z: gen.Z + gen.Height/2}
		dx, dz := core.X-mid.X, core.Z-mid.Z
		doorway := map[domain.Cell]bool{}
		for t := int32(1); t <= geothermalShell; t++ {
			c := mid
			switch {
			case abs32(dx) >= abs32(dz) && dx >= 0:
				c.X = gen.X + gen.Width - 1 + t
			case abs32(dx) >= abs32(dz):
				c.X = gen.X - t
			case dz >= 0:
				c.Z = gen.Z + gen.Height - 1 + t
			default:
				c.Z = gen.Z - t
			}
			doorway[c] = true
		}
		s := PerimeterSection{Name: TierGeothermal}
		for _, c := range rectCells(geothermal) {
			if contains(gen, c) {
				continue
			}
			def := wall
			if doorway[c] {
				def = door
			}
			b, err := domain.NewBuilding(def, c, domain.North, "")
			if err != nil {
				return nil, err
			}
			s.Buildings = append(s.Buildings, b)
		}
		out = append(out, s)
	}
	return out, nil
}

func unionRect(a, b Rectangle) Rectangle {
	if a.Width == 0 {
		return b
	}
	if b.Width == 0 {
		return a
	}
	x0, z0 := min(a.X, b.X), min(a.Z, b.Z)
	x1, z1 := max(a.X+a.Width, b.X+b.Width), max(a.Z+a.Height, b.Z+b.Height)
	return Rectangle{X: x0, Z: z0, Width: x1 - x0, Height: z1 - z0}
}
