package policy

import (
	"crypto/sha256"
	"fmt"
	"regexp"
	"slices"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The defense layout builds on the layout plan: the corridor,
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

// The perimeter's footings on soft ground: a plain bridge holds a
// wooden wall only; a heavy bridge, once researched, holds stone.
const (
	PerimeterBridge        = "Bridge"
	PerimeterHeavyBridge   = "HeavyBridge"
	PerimeterHeavyResearch = "HeavyBridges"
	PerimeterLightStuff    = "WoodLog"
)

// PerimeterSection is one staged piece of the wall. Nodes are the plan's site
// nodes it is cut from (the ring and the gates in it, a pump, the fence, the
// geothermal enclosure): the defense record stores them instead of the
// buildings, and the buildings are resolved from the plan again by the
// section's name. A section cut from no node (lights, pockets, prison
// turrets) names none.
type PerimeterSection struct {
	Name      DefenseTierName
	Nodes     []string
	Buildings []domain.Building
}

// PerimeterSectionBase is the section's name before a re-cut put its
// revision in front: perimeter-r2-03 and perimeter-03 are both "03".
func PerimeterSectionBase(name DefenseTierName) string {
	rest := strings.TrimPrefix(string(name), TierPerimeterPrefix)
	if m := perimeterRevision.FindString(rest); m != "" {
		rest = rest[len(m):]
	}
	return rest
}

var perimeterRevision = regexp.MustCompile(`^r\d+-`)

// KillboxNode is the live killbox node's id and a digest of its footprint,
// the identity a stored defense layout was proposed on. ok is false while the
// plan has no opening.
func KillboxNode(plan LayoutPlan) (id, digest string, ok bool) {
	node, found := plan.Killbox()
	if !found {
		return "", "", false
	}
	h := sha256.New()
	for _, c := range node.Footprint.Cells() {
		fmt.Fprintf(h, "%d,%d;", c.X, c.Z)
	}
	return node.Node, fmt.Sprintf("%x", h.Sum(nil)[:8]), true
}

// KillboxOpening reads the plan's killbox opening alone: the anchor cell, the
// direction toward home, the width and depth. ok is false while the plan has no
// opening.
func KillboxOpening(plan LayoutPlan) (k DefenseKillbox, ok bool) {
	k, _, _, ok = layoutOpening(plan)
	return k, ok
}

// OpeningPlan is the smallest valid layout plan whose killbox opens toward the
// given rotation: a 21x21 killbox beyond the entry cell (30, 30), the 3-wide approach leg
// ending beside the entry cell and one room to make the plan valid. A fixture
// or replay that stands for a world's plan only for the direction combat
// reads from it builds this.
func OpeningPlan(toward domain.Rotation) LayoutPlan {
	// The killbox lies on the far side of the entry, past the wall's depth.
	approach, killbox := Rectangle{X: 29, Z: 29, Width: 3, Height: 1}, Rectangle{X: 20, Z: 33, Width: 21, Height: 21}
	switch toward {
	case domain.South:
		approach, killbox = Rectangle{X: 29, Z: 31, Width: 3, Height: 1}, Rectangle{X: 20, Z: 7, Width: 21, Height: 21}
	case domain.East:
		approach, killbox = Rectangle{X: 29, Z: 29, Width: 1, Height: 3}, Rectangle{X: 33, Z: 20, Width: 21, Height: 21}
	case domain.West:
		approach, killbox = Rectangle{X: 31, Z: 29, Width: 1, Height: 3}, Rectangle{X: 7, Z: 20, Width: 21, Height: 21}
	}
	site := func(id string, kind NodeKind, r Rectangle) PlannedSite {
		return PlannedSite{SitePlacement: SitePlacement{Node: id, Footprint: RectRegion(r)}, Kind: kind}
	}
	return LayoutPlan{
		Rooms:      []PlannedRoom{{ID: 1, Role: PlannedStorage, Region: RectRegion(Rectangle{X: 10, Z: 10, Width: 6, Height: 4}), Door: domain.Cell{X: 12, Z: 14}, DoorRot: domain.South}},
		NextRoomID: 2,
		Sites: []PlannedSite{
			site("killbox", NodeKillbox, killbox),
			site("killbox_approach-00", NodeApproach, approach),
		},
	}
}

// LayoutKillbox reads the plan's killbox opening: the anchor, the census
// region around it (inside bounds) and the home
// cell behind the killbox's back wall. ok is false while the plan has no opening.
func LayoutKillbox(plan LayoutPlan, bounds Bounds) (k DefenseKillbox, region Rectangle, home domain.Cell, ok bool) {
	k, killbox, approaches, ok := layoutOpening(plan)
	if !ok {
		return DefenseKillbox{}, Rectangle{}, domain.Cell{}, false
	}
	// Home is the yard cell behind the killbox's back-wall doorway.
	home = addCell(k.Entry, scale(directionOf(k.Toward), perimeterThick+k.Depth))
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

// layoutOpening is the opening LayoutKillbox and KillboxOpening read: the
// anchor with the whole ring as its walled cells, the killbox rectangle and
// the approach legs.
func layoutOpening(plan LayoutPlan) (k DefenseKillbox, killbox Rectangle, approaches []Rectangle, ok bool) {
	if node, found := plan.Killbox(); found {
		killbox = node.Footprint.Bounds()
	}
	for _, a := range plan.LiveSites(NodeApproach) {
		approaches = append(approaches, a.Footprint.Bounds())
	}
	if ring, found := plan.Ring(); found {
		k.Walled = ring.Footprint.Cells()
	}
	if killbox.Width == 0 {
		return DefenseKillbox{}, Rectangle{}, nil, false
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
				k.Entry, k.Toward, k.Width, k.Depth, found = entry, rotationOf(d), 3, killbox.Height, true
				if d.X != 0 {
					k.Depth = killbox.Width
				}
			}
		}
	}
	if !found {
		return DefenseKillbox{}, Rectangle{}, nil, false
	}
	return k, killbox, approaches, true
}

// PerimeterSections cuts the plan's wall into sections of about 60 blocks,
// nearest the killbox first: walls, with a door at each end of every gate
// and the cell between left open, an airlock. The geothermal site's
// shell, when the plan holds one, is its own section with a doorway of doors facing the
// core. A wall on water stands on bridge, laid by a section of its
// own just before the wall's; a wall on light footing or on a plain Bridge
// is WoodLog. Other stuff is left empty; the planner fills in the stone at
// admission.
func PerimeterSections(plan LayoutPlan, wall, door, bridge string) ([]PerimeterSection, error) {
	gates, airlock, light, bridged := map[domain.Cell]bool{}, map[domain.Cell]bool{}, map[domain.Cell]bool{}, map[domain.Cell]bool{}
	woodInner := map[domain.Cell]bool{} // light-footing wall cells: wood to start
	var runs []Rectangle
	var killbox, geothermal Rectangle
	gateNode := map[domain.Cell]string{}
	for _, g := range plan.LiveSites(NodeGate) {
		// An airlock: a door on each face of the wall, the
		// cells between left open, enclosed by the wall beside them.
		ends := g.Footprint.Bounds().Width
		if g.Footprint.Bounds().Height > ends {
			ends = g.Footprint.Bounds().Height
		}
		for i, c := range g.Footprint.Cells() {
			gateNode[c] = g.Node
			if i == 0 || int32(i) == ends-1 {
				gates[c] = true
			} else {
				airlock[c] = true
			}
		}
	}
	ringID := ""
	if ring, found := plan.Ring(); found {
		ringID = ring.Node
		runs = regionRects(ring.Footprint)
		for _, c := range ring.Ring.Light.Cells() {
			woodInner[c] = true
		}
		for _, c := range ring.Ring.Bridged.Cells() {
			bridged[c] = true
		}
	}
	if k, found := plan.Killbox(); found {
		killbox = k.Footprint.Bounds()
	}
	for _, r := range plan.Reservations {
		if r.Kind == ReserveGeothermal && geothermal.Width == 0 {
			geothermal = r.Area.Bounds()
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
		return PerimeterSection{Name: DefenseTierName(fmt.Sprintf("%s%02d", TierPerimeterPrefix, len(out))), Nodes: []string{ringID}}
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
		section := next()
		for _, c := range rectCells(p.area) {
			if airlock[c] {
				continue
			}
			def, stuff := wall, ""
			if gates[c] {
				def = door
			}
			if light[c] || woodInner[c] && !gates[c] || bridged[c] && bridge == PerimeterBridge {
				stuff = PerimeterLightStuff
			}
			b, err := domain.NewBuilding(def, c, domain.North, stuff)
			if err != nil {
				return nil, err
			}
			section.Buildings = append(section.Buildings, b)
			if id := gateNode[c]; id != "" && !slices.Contains(section.Nodes, id) {
				section.Nodes = append(section.Nodes, id)
			}
		}
		if len(section.Buildings) > 0 {
			out = append(out, section)
		}
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
		s := PerimeterSection{Name: TierGeothermal, Nodes: []string{"geothermal-1"}}
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

// TierPumpPrefix names the moisture pump sections under the perimeter's
// prefix: one per pump site, with its conduit run.
const TierPumpPrefix = TierPerimeterPrefix + "pump-"

// PerimeterPumps is one section per moisture pump site: the pump
// and the conduit run bringing a transmitter within connector reach,
// chained over the ring's interior clear of the killbox. A run laid for an
// earlier pump carries the later ones. Nothing without a transmitter to
// join: an unpowered pump dries nothing.
func PerimeterPumps(plan LayoutPlan, pump, conduit string, transmitters []domain.Cell) ([]PerimeterSection, error) {
	var killbox Rectangle
	var sites []domain.Cell
	var ids []string
	if k, found := plan.Killbox(); found {
		killbox = k.Footprint.Bounds()
	}
	for _, p := range plan.LiveSites(NodePump) {
		sites, ids = append(sites, p.Origin), append(ids, p.Node)
	}
	if len(sites) == 0 || len(transmitters) == 0 {
		return nil, nil
	}
	wi, _ := planInterior(plan, 0)
	allowed, carry := map[domain.Cell]bool{}, map[domain.Cell]bool{}
	for _, c := range wi.cells() {
		allowed[c] = !contains(killbox, c)
	}
	for _, c := range sites {
		allowed[c] = false
	}
	for _, c := range transmitters {
		allowed[c], carry[c] = true, true
	}
	var out []PerimeterSection
	for i, site := range sites {
		chain, ok := defenseSite{}.conduitChain(site, carry, allowed)
		if !ok {
			continue
		}
		s := PerimeterSection{Name: DefenseTierName(fmt.Sprintf("%s%02d", TierPumpPrefix, len(out))), Nodes: []string{ids[i]}}
		b, err := domain.NewBuilding(pump, site, domain.North, "")
		if err != nil {
			return nil, err
		}
		s.Buildings = append(s.Buildings, b)
		for _, c := range chain {
			if b, err = domain.NewBuilding(conduit, c, domain.North, ""); err != nil {
				return nil, err
			}
			s.Buildings = append(s.Buildings, b)
			carry[c] = true
		}
		out = append(out, s)
	}
	return out, nil
}

// TierFencePrefix names the killbox opening's fence section under the
// perimeter's prefix.
const TierFencePrefix = TierPerimeterPrefix + "fence-"

// FenceSections is one section for the fence cells across the killbox
// opening (NodeFence): a Fence is PassThroughOnly, stopping only
// roamers, so the lane stays the raiders' way in.
func FenceSections(plan LayoutPlan, fence, stuff string) ([]PerimeterSection, error) {
	var out []PerimeterSection
	for _, f := range plan.LiveSites(NodeFence) {
		s := PerimeterSection{Name: DefenseTierName(fmt.Sprintf("%s%02d", TierFencePrefix, len(out))), Nodes: []string{f.Node}}
		for _, c := range f.Footprint.Cells() {
			b, err := domain.NewBuilding(fence, c, domain.North, stuff)
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
