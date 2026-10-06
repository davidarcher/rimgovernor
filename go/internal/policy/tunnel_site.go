package policy

import (
	"errors"
	"fmt"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Tunnel siting (#1072): a 1-wide corridor driven into a visible natural
// rock face from a walkable access cell, ending beside a buried ore deposit
// that mining then opens. Dug rooms are the layout plan's (#1250); this
// only reaches ore.
//
// Fogged cells are unknown, never assumed empty or safe: a corridor may run
// through fogged space, but any known cell inside or around it must be rock.
// Native ReadExcavationSite remains the authority for roof support and
// per-cell mining legality; this file only proposes geometry.

// excavationMaxCells matches NativeExcavationSite.MaxCells so a whole target
// fits in one site read.
const excavationMaxCells = 64

type ExcavationSiteRequest struct {
	Bounds Bounds
	// Region is the observed window: a cell absent from Cells inside it is
	// fogged (unknown); outside it nothing was read, so no target may touch it.
	Region    Rectangle
	Anchor    domain.Cell
	Cells     []SiteCell
	Protected []domain.Cell
	// MinCorridor..MaxCorridor bound the corridor length tried per face.
	MinCorridor, MaxCorridor int32
	// RoofSupport is the game's roof support radius (the catalog's
	// roof_max_support_distance): every target cell must lie within it of
	// some non-target cell.
	RoofSupport float64
	// Roofs are the load's roof rules (#1890): a roofed cell whose def is not
	// in them fails the site read with ErrUnknownRoof.
	Roofs RoofRules
}

// ExcavationTarget is one proposed tunnel: Access is the walkable cell the
// corridor starts from, Corridor runs inward from the face (Corridor[0] is
// the face cell) and Door is its last cell, beside the ore.
type ExcavationTarget struct {
	Access    domain.Cell
	Direction domain.Cell
	Corridor  []domain.Cell
	Door      domain.Cell
	Score     int64
}

// Cells returns the ordered target, face first, so a frontier walk digs the
// way a miner would.
func (t ExcavationTarget) Cells() []domain.Cell {
	return append([]domain.Cell(nil), t.Corridor...)
}

// Key is a compact, reversible identity for the target so per-stage plan
// identities survive restarts and cleared cells without any extra table:
// "<accessX>.<accessZ>.<dirX>.<dirZ>.<length>".
func (t ExcavationTarget) Key() string {
	return fmt.Sprintf("%d.%d.%d.%d.%d", t.Access.X, t.Access.Z, t.Direction.X, t.Direction.Z, len(t.Corridor))
}

// ParseExcavationKey rebuilds a target from Key. It validates shape only;
// the caller re-reads the site before trusting it, and the native site read
// is the authority on roof support.
func ParseExcavationKey(key string) (ExcavationTarget, error) {
	invalid := errors.New("invalid excavation key")
	parts := strings.Split(key, ".")
	if len(parts) != 5 {
		return ExcavationTarget{}, invalid
	}
	var v [5]int32
	for i, p := range parts {
		if _, err := fmt.Sscanf(p, "%d", &v[i]); err != nil || fmt.Sprint(v[i]) != p {
			return ExcavationTarget{}, invalid
		}
	}
	t := ExcavationTarget{Access: domain.Cell{X: v[0], Z: v[1]}, Direction: domain.Cell{X: v[2], Z: v[3]}}
	length := v[4]
	if (t.Direction.X == 0) == (t.Direction.Z == 0) || t.Direction.X < -1 || t.Direction.X > 1 || t.Direction.Z < -1 || t.Direction.Z > 1 || length <= 0 || length > excavationMaxCells || t.Access.X < 0 || t.Access.Z < 0 {
		return ExcavationTarget{}, invalid
	}
	for i := int32(1); i <= length; i++ {
		t.Corridor = append(t.Corridor, domain.Cell{X: t.Access.X + t.Direction.X*i, Z: t.Access.Z + t.Direction.Z*i})
	}
	t.Door = t.Corridor[len(t.Corridor)-1]
	if t.Corridor[0].X < 0 || t.Corridor[0].Z < 0 {
		return ExcavationTarget{}, invalid
	}
	return t, nil
}

// excavationSupported reports whether every target cell lies within
// RimWorld's roof support radius of some cell outside the target, so the
// rock left standing supports the roof over the whole dig.
func excavationSupported(cells []domain.Cell, support float64) bool {
	inside := make(map[domain.Cell]bool, len(cells))
	for _, c := range cells {
		inside[c] = true
	}
	r := int32(support)
	for _, c := range cells {
		held := false
		for dz := -r; dz <= r && !held; dz++ {
			for dx := -r; dx <= r; dx++ {
				if float64(dx*dx+dz*dz) <= support*support && !inside[domain.Cell{X: c.X + dx, Z: c.Z + dz}] {
					held = true
					break
				}
			}
		}
		if !held {
			return false
		}
	}
	return true
}

// rockCell is a visible natural rock cell: solid, impassable natural rock,
// roofed or not (the rim of a mountain carries no roof, and a miner reaches
// the rock behind it only by mining it).
func rockCell(c SiteCell) bool {
	walkable, wk := c.Walkable.Value()
	return c.Occupied() && wk && !walkable && c.NaturalRock()
}

// excavatedCell is open ground still under a natural rock roof that is not
// part of a proper room: rock that has already been dug (or a natural
// pocket), so a half-dug tunnel is re-planned over the same cells.
//
// Only a natural roof (RoofRule.Natural) marks a cell as part of a mountain,
// so a constructed wall under a built roof is never mistaken for a rock face.
func excavatedCell(c SiteCell, roofs RoofRules) bool {
	walkable, wk := c.Walkable.Value()
	roof, rk := c.Roof.Value()
	indoors, ik := c.Indoors.Value()
	return !c.Occupied() && wk && walkable && rk && roofs[roof].Natural && (!ik || !indoors)
}

// CorridorExcavationSites proposes corridor targets whose door is
// cardinally adjacent to the buried ore cell, best first. A target is
// rejected when any known cell inside it is not rock, when any known cell
// cardinally bordering it (other than the access cell) is passable, or when
// it touches the map edge.
func CorridorExcavationSites(r ExcavationSiteRequest, ore domain.Cell) ([]ExcavationTarget, error) {
	if r.Bounds.Width <= 0 || r.Bounds.Height <= 0 || r.Bounds.Width > 4096 || r.Bounds.Height > 4096 {
		return nil, errors.New("invalid excavation site bounds")
	}
	if r.MinCorridor <= 0 || r.MaxCorridor < r.MinCorridor || r.MaxCorridor > excavationMaxCells {
		return nil, errors.New("invalid excavation corridor")
	}
	if !(r.RoofSupport > 0) {
		return nil, errors.New("invalid excavation roof support distance")
	}
	inBounds := func(c domain.Cell) bool { return c.X >= 0 && c.Z >= 0 && c.X < r.Bounds.Width && c.Z < r.Bounds.Height }
	if r.Region.Width <= 0 || r.Region.Height <= 0 || !inBounds(domain.Cell{X: r.Region.X, Z: r.Region.Z}) || !inBounds(domain.Cell{X: r.Region.X + r.Region.Width - 1, Z: r.Region.Z + r.Region.Height - 1}) {
		return nil, errors.New("invalid observed region")
	}
	observed := func(c domain.Cell) bool {
		return c.X >= r.Region.X && c.Z >= r.Region.Z && c.X < r.Region.X+r.Region.Width && c.Z < r.Region.Z+r.Region.Height
	}
	// Digging against the map edge leaves no rock to hold the roof there.
	interior := func(c domain.Cell) bool {
		return observed(c) && c.X >= 1 && c.Z >= 1 && c.X < r.Bounds.Width-1 && c.Z < r.Bounds.Height-1
	}
	if !inBounds(r.Anchor) {
		return nil, errors.New("invalid colony anchor")
	}
	cells := make(map[domain.Cell]SiteCell, len(r.Cells))
	ordered := make([]domain.Cell, 0, len(r.Cells))
	for _, c := range r.Cells {
		if !inBounds(c.Cell) {
			return nil, errors.New("site cell out of bounds")
		}
		if _, exists := cells[c.Cell]; exists {
			return nil, errors.New("duplicate site cell")
		}
		if roof, ok := c.Roof.Value(); ok && roof != "" {
			if _, known := r.Roofs[roof]; !known {
				return nil, fmt.Errorf("%w %q at %v", ErrUnknownRoof, roof, c.Cell)
			}
		}
		cells[c.Cell] = c
		ordered = append(ordered, c.Cell)
	}
	sort.Slice(ordered, func(i, j int) bool { return cellLess(ordered[i], ordered[j]) })
	protected := map[domain.Cell]bool{}
	for _, c := range r.Protected {
		if !inBounds(c) {
			return nil, errors.New("protected cell out of bounds")
		}
		protected[c] = true
	}
	free := func(p domain.Cell) bool {
		c, exists := cells[p]
		return exists && !protected[p] && positive(c.Walkable) && !c.Occupied()
	}
	// diggable: unknown (fogged), visible rock or already excavated, never
	// protected.
	diggable := func(p domain.Cell) bool {
		if !interior(p) || protected[p] {
			return false
		}
		c, exists := cells[p]
		return !exists || rockCell(c) || excavatedCell(c, r.Roofs)
	}
	// sealed: a bordering cell that is unknown or known impassable keeps the
	// tunnel enclosed once dug.
	sealed := func(p domain.Cell) bool {
		if !inBounds(p) || !observed(p) {
			return false
		}
		c, exists := cells[p]
		if !exists {
			return true
		}
		walkable, wk := c.Walkable.Value()
		return wk && !walkable
	}
	// judge accepts one corridor when every cell is diggable, at least one
	// is rock, and its cardinal neighbours are sealed; it returns the score.
	judge := func(t ExcavationTarget) (int64, bool) {
		inside := make(map[domain.Cell]bool, len(t.Corridor))
		work, done := false, int64(0)
		for _, p := range t.Corridor {
			if !diggable(p) {
				return 0, false
			}
			inside[p] = true
			if c, exists := cells[p]; !exists || rockCell(c) {
				work = true
			} else {
				done++
			}
		}
		// An already-open corridor is not an excavation.
		if !work {
			return 0, false
		}
		for _, p := range t.Corridor {
			for _, d := range []domain.Cell{{X: 0, Z: 1}, {X: 1, Z: 0}, {X: 0, Z: -1}, {X: -1, Z: 0}} {
				n := domain.Cell{X: p.X + d.X, Z: p.Z + d.Z}
				if inside[n] || n == t.Access || n == ore {
					continue
				}
				if !sealed(n) {
					return 0, false
				}
			}
		}
		// Work already done outweighs distance: a half-dug tunnel wins
		// over a fresh face a cell nearer.
		return squaredDistance(t.Door, r.Anchor) + 4*int64(len(t.Corridor)) - done*4096, true
	}
	directions := []domain.Cell{{X: 0, Z: 1}, {X: 1, Z: 0}, {X: 0, Z: -1}, {X: -1, Z: 0}}
	var targets []ExcavationTarget
	for _, access := range ordered {
		if !free(access) {
			continue
		}
		for _, d := range directions {
			face := domain.Cell{X: access.X + d.X, Z: access.Z + d.Z}
			c, exists := cells[face]
			if !exists || !(rockCell(c) || excavatedCell(c, r.Roofs)) {
				continue
			}
			for length := r.MinCorridor; length <= r.MaxCorridor; length++ {
				corridor := make([]domain.Cell, 0, length)
				through := false
				for i := int32(1); i <= length; i++ {
					p := domain.Cell{X: access.X + d.X*i, Z: access.Z + d.Z*i}
					through = through || p == ore
					corridor = append(corridor, p)
				}
				if through {
					break
				}
				t := ExcavationTarget{Access: access, Direction: d, Corridor: corridor, Door: corridor[len(corridor)-1]}
				dx, dz := t.Door.X-ore.X, t.Door.Z-ore.Z
				if dx*dx+dz*dz != 1 || !excavationSupported(t.Corridor, r.RoofSupport) {
					continue
				}
				if score, ok := judge(t); ok {
					t.Score = score
					targets = append(targets, t)
					break
				}
			}
		}
	}
	sort.Slice(targets, func(i, j int) bool {
		if targets[i].Score != targets[j].Score {
			return targets[i].Score < targets[j].Score
		}
		if targets[i].Access != targets[j].Access {
			return cellLess(targets[i].Access, targets[j].Access)
		}
		return cellLess(targets[i].Direction, targets[j].Direction)
	})
	if len(targets) > 12 {
		targets = targets[:12]
	}
	return targets, nil
}
