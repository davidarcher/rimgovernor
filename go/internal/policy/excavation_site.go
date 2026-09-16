package policy

import (
	"errors"
	"fmt"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Excavation site selection for B06f: a 1-wide corridor driven into a
// visible natural rock face from a walkable access cell, ending in a
// rectangular interior. Targets are ordered cell sets so irregular shapes
// can replace the rectangle later without changing the planner.
//
// Fogged cells are unknown, never assumed empty or safe: a target may run
// through fogged space (that is the whole point of a mountain base), but
// any known cell inside or around it must be rock. Native
// ReadExcavationSite remains the authority for roof support and per-cell
// mining legality; this file only proposes geometry.

// Natural rock roofs. Only these mark a cell as part of a mountain, so a
// constructed wall under a built roof is never mistaken for a rock face.
var rockRoofs = map[string]bool{"RoofRockThin": true, "RoofRockThick": true}

// excavationSupportSpan bounds the interior so every target cell stays within
// RimWorld's six-cell roof support radius of a non-target cell; a 1-wide
// corridor trivially satisfies it.
const excavationSupportSpan = 11

// excavationSunkWorkWeight is the score credit per already-open target cell,
// larger than any squared distance within the observed window so a target
// with sunk work always ranks first.
const excavationSunkWorkWeight = 4096

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
	// Interior is the room size in cells (walls are the surrounding rock).
	Interior Bounds
	// MinCorridor..MaxCorridor bound the corridor length tried per face.
	MinCorridor, MaxCorridor int32
}

// ExcavationTarget is one proposed dig: Access is the walkable cell the
// corridor starts from, Corridor runs inward from the face (Corridor[0] is
// the face cell), Door is the last corridor cell, and Interior is the room.
type ExcavationTarget struct {
	Access    domain.Cell
	Direction domain.Cell
	Corridor  []domain.Cell
	Interior  Rectangle
	Door      domain.Cell
	Score     int64
}

// Cells returns the ordered target: corridor first, then interior rows from
// the door outward, so a frontier walk digs the way a miner would.
func (t ExcavationTarget) Cells() []domain.Cell {
	out := append([]domain.Cell(nil), t.Corridor...)
	interior := rectCells(t.Interior)
	sort.Slice(interior, func(i, j int) bool {
		a, b := squaredDistance(interior[i], t.Door), squaredDistance(interior[j], t.Door)
		if a != b {
			return a < b
		}
		return cellLess(interior[i], interior[j])
	})
	return append(out, interior...)
}

// Center is the interior centre used for anchor distance comparisons.
func (t ExcavationTarget) Center() domain.Cell {
	return domain.Cell{X: t.Interior.X + t.Interior.Width/2, Z: t.Interior.Z + t.Interior.Height/2}
}

// Key is a compact, reversible identity for the target so per-stage plan
// identities survive restarts and cleared cells without any extra table.
func (t ExcavationTarget) Key() string {
	return fmt.Sprintf("%d.%d.%d.%d.%d.%d.%d.%d.%d", t.Access.X, t.Access.Z, t.Direction.X, t.Direction.Z, len(t.Corridor), t.Interior.X, t.Interior.Z, t.Interior.Width, t.Interior.Height)
}

// ParseExcavationKey rebuilds a target from Key. It validates shape only;
// the caller re-reads the site before trusting it.
func ParseExcavationKey(key string) (ExcavationTarget, error) {
	parts := strings.Split(key, ".")
	if len(parts) != 9 {
		return ExcavationTarget{}, errors.New("invalid excavation key")
	}
	var v [9]int32
	for i, p := range parts {
		if _, err := fmt.Sscanf(p, "%d", &v[i]); err != nil || fmt.Sprint(v[i]) != p {
			return ExcavationTarget{}, errors.New("invalid excavation key")
		}
	}
	t := ExcavationTarget{Access: domain.Cell{X: v[0], Z: v[1]}, Direction: domain.Cell{X: v[2], Z: v[3]}, Interior: Rectangle{v[5], v[6], v[7], v[8]}}
	length := v[4]
	if (t.Direction.X == 0) == (t.Direction.Z == 0) || t.Direction.X < -1 || t.Direction.X > 1 || t.Direction.Z < -1 || t.Direction.Z > 1 || length <= 0 || length > excavationMaxCells || t.Interior.Width <= 0 || t.Interior.Height <= 0 || t.Interior.Width > excavationSupportSpan || t.Interior.Height > excavationSupportSpan || t.Access.X < 0 || t.Access.Z < 0 || t.Interior.X < 0 || t.Interior.Z < 0 {
		return ExcavationTarget{}, errors.New("invalid excavation key")
	}
	for i := int32(1); i <= length; i++ {
		t.Corridor = append(t.Corridor, domain.Cell{X: t.Access.X + t.Direction.X*i, Z: t.Access.Z + t.Direction.Z*i})
	}
	t.Door = t.Corridor[len(t.Corridor)-1]
	if len(t.Cells()) > excavationMaxCells || t.Corridor[0].X < 0 || t.Corridor[0].Z < 0 {
		return ExcavationTarget{}, errors.New("invalid excavation key")
	}
	expected := excavationInterior(t.Door, t.Direction, Bounds{Width: t.Interior.Width, Height: t.Interior.Height})
	if expected != t.Interior {
		return ExcavationTarget{}, errors.New("invalid excavation key")
	}
	return t, nil
}

// excavationInterior centres a size-by-size room just past the door along
// the corridor direction.
func excavationInterior(door, direction domain.Cell, size Bounds) Rectangle {
	next := domain.Cell{X: door.X + direction.X, Z: door.Z + direction.Z}
	switch {
	case direction.Z > 0:
		return Rectangle{next.X - size.Width/2, next.Z, size.Width, size.Height}
	case direction.Z < 0:
		return Rectangle{next.X - size.Width/2, next.Z - size.Height + 1, size.Width, size.Height}
	case direction.X > 0:
		return Rectangle{next.X, next.Z - size.Height/2, size.Width, size.Height}
	default:
		return Rectangle{next.X - size.Width + 1, next.Z - size.Height/2, size.Width, size.Height}
	}
}

// rockCell is a visible natural rock cell: solid, impassable, under a rock roof.
func rockCell(c SiteCell) bool {
	occupied, ok := c.Occupied.Value()
	walkable, wk := c.Walkable.Value()
	roof, rk := c.Roof.Value()
	return ok && occupied && wk && !walkable && rk && rockRoofs[roof]
}

// excavatedCell is open ground still under a natural rock roof that is not
// part of a proper room: rock that has already been dug (or a natural
// pocket). A project whose goal was invalidated mid-way is re-planned over
// the same cells instead of abandoning the half-dug room.
func excavatedCell(c SiteCell) bool {
	occupied, ok := c.Occupied.Value()
	walkable, wk := c.Walkable.Value()
	roof, rk := c.Roof.Value()
	indoors, ik := c.Indoors.Value()
	return ok && !occupied && wk && walkable && rk && rockRoofs[roof] && (!ik || !indoors)
}

// ExcavationSites proposes corridor+room targets for every visible rock face
// reachable from a free access cell, best first. A target is rejected when
// any known cell inside it is not rock, when any known cell bordering it
// (other than the access cell) is passable, or when it touches the map edge.
func ExcavationSites(r ExcavationSiteRequest) ([]ExcavationTarget, error) {
	if r.Bounds.Width <= 0 || r.Bounds.Height <= 0 || r.Bounds.Width > 4096 || r.Bounds.Height > 4096 || len(r.Cells) > 65536 || len(r.Protected) > 65536 {
		return nil, errors.New("invalid excavation site bounds")
	}
	if r.Interior.Width <= 0 || r.Interior.Height <= 0 || r.Interior.Width > excavationSupportSpan || r.Interior.Height > excavationSupportSpan || r.MinCorridor <= 0 || r.MaxCorridor < r.MinCorridor || int(r.Interior.Width)*int(r.Interior.Height)+int(r.MaxCorridor) > excavationMaxCells {
		return nil, errors.New("invalid excavation interior")
	}
	inBounds := func(c domain.Cell) bool { return c.X >= 0 && c.Z >= 0 && c.X < r.Bounds.Width && c.Z < r.Bounds.Height }
	if r.Region.Width <= 0 || r.Region.Height <= 0 || !inBounds(domain.Cell{X: r.Region.X, Z: r.Region.Z}) || !inBounds(domain.Cell{X: r.Region.X + r.Region.Width - 1, Z: r.Region.Z + r.Region.Height - 1}) {
		return nil, errors.New("invalid observed region")
	}
	observed := func(c domain.Cell) bool {
		return c.X >= r.Region.X && c.Z >= r.Region.Z && c.X < r.Region.X+r.Region.Width && c.Z < r.Region.Z+r.Region.Height
	}
	// Digging against the map edge would open the room to the outside and
	// leaves no rock to hold the roof there.
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
		return exists && !protected[p] && positive(c.Walkable) && positive(measured(c.Occupied, func(v bool) bool { return !v }))
	}
	// diggable: unknown (fogged), visible rock or already excavated, never
	// protected.
	diggable := func(p domain.Cell) bool {
		if !interior(p) || protected[p] {
			return false
		}
		c, exists := cells[p]
		return !exists || rockCell(c) || excavatedCell(c)
	}
	// sealed: a bordering cell that is unknown or known impassable keeps the
	// room enclosed once dug.
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
	directions := []domain.Cell{{X: 0, Z: 1}, {X: 1, Z: 0}, {X: 0, Z: -1}, {X: -1, Z: 0}}
	var targets []ExcavationTarget
	seen := map[string]bool{}
	for _, access := range ordered {
		if !free(access) {
			continue
		}
		for _, d := range directions {
			face := domain.Cell{X: access.X + d.X, Z: access.Z + d.Z}
			c, exists := cells[face]
			if !exists || !(rockCell(c) || excavatedCell(c)) {
				continue
			}
			for length := r.MinCorridor; length <= r.MaxCorridor; length++ {
				t := ExcavationTarget{Access: access, Direction: d}
				for i := int32(1); i <= length; i++ {
					t.Corridor = append(t.Corridor, domain.Cell{X: access.X + d.X*i, Z: access.Z + d.Z*i})
				}
				t.Door = t.Corridor[len(t.Corridor)-1]
				t.Interior = excavationInterior(t.Door, d, r.Interior)
				if seen[t.Key()] {
					continue
				}
				seen[t.Key()] = true
				target := t.Cells()
				inside := make(map[domain.Cell]bool, len(target))
				legal, work, done := true, false, int64(0)
				for _, p := range target {
					if !diggable(p) {
						legal = false
						break
					}
					inside[p] = true
					if c, exists := cells[p]; !exists || rockCell(c) {
						work = true
					} else {
						done++
					}
				}
				// An already-open target is not an excavation.
				if !legal || !work {
					continue
				}
				// The room must be sealed on all eight sides (its door is
				// the corridor); corridor cells only need their cardinal
				// neighbours sealed, since the mouth diagonals face open
				// ground by construction.
				for i, p := range target {
					corridor := i < len(t.Corridor)
					for dx := int32(-1); dx <= 1 && legal; dx++ {
						for dz := int32(-1); dz <= 1; dz++ {
							n := domain.Cell{X: p.X + dx, Z: p.Z + dz}
							if inside[n] || n == access || corridor && dx != 0 && dz != 0 {
								continue
							}
							if !sealed(n) {
								legal = false
								break
							}
						}
					}
				}
				if !legal {
					continue
				}
				// Work already done outweighs distance: the colony anchor
				// drifts with the pawns, and a half-dug room must win over
				// a fresh face a cell nearer.
				t.Score = squaredDistance(t.Center(), r.Anchor) + 4*int64(length) - done*excavationSunkWorkWeight
				targets = append(targets, t)
				// A longer corridor from the same face only helps when the
				// shorter one was illegal.
				break
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

// excavationReach bounds how far from the colony anchor an excavated room
// may sit before the open-site shell is preferred; it matches the native
// planning window so every candidate the policy can see is also reachable
// without a long trek.
const excavationReach = 22

// ChooseExcavation decides between the open-site starter shell and digging
// in, deterministically and without a tuning knob. A rock room needs no wall
// material and holds its own roof, so a verified site within reach of the
// anchor wins outright; farther sites win only when no clean shell exists,
// and a shell nearer the anchor than a far site keeps the shell.
func ChooseExcavation(anchor domain.Cell, shell *StarterLayout, site *ExcavationTarget) bool {
	if site == nil {
		return false
	}
	if shell == nil || squaredDistance(site.Center(), anchor) <= excavationReach*excavationReach {
		return true
	}
	shellCenter := domain.Cell{X: shell.Room.X + shell.Room.Width/2, Z: shell.Room.Z + shell.Room.Height/2}
	return squaredDistance(site.Center(), anchor) < squaredDistance(shellCenter, anchor)
}

// ExcavationCellState is the per-cell answer the frontier walks over,
// projected from a native site read.
type ExcavationCellState struct {
	Cell     domain.Cell
	Fogged   bool
	Cleared  bool
	Eligible bool
	Rock     string
}

// ExcavationFrontier returns the next cells to designate, in target order,
// bounded by stageLimit: uncleared, visible, natively eligible cells that a
// miner can reach through the access cell, a cleared cell, or another cell
// of the same stage. It reports unknown when nothing is designatable but
// the remainder is still fogged, so the caller re-observes instead of
// declaring the site blocked.
func ExcavationFrontier(target ExcavationTarget, states []ExcavationCellState, stageLimit int) (stage []domain.Cell, remaining int, unknown bool) {
	order := target.Cells()
	byCell := make(map[domain.Cell]ExcavationCellState, len(states))
	for _, s := range states {
		byCell[s.Cell] = s
	}
	open := map[domain.Cell]bool{target.Access: true}
	for _, c := range order {
		if s, ok := byCell[c]; ok && s.Cleared {
			open[c] = true
		}
	}
	adjacentOpen := func(c domain.Cell) bool {
		for _, d := range []domain.Cell{{X: 0, Z: 1}, {X: 1, Z: 0}, {X: 0, Z: -1}, {X: -1, Z: 0}} {
			if open[domain.Cell{X: c.X + d.X, Z: c.Z + d.Z}] {
				return true
			}
		}
		return false
	}
	for _, c := range order {
		s, ok := byCell[c]
		if !ok {
			unknown = true
			remaining++
			continue
		}
		if s.Cleared {
			continue
		}
		remaining++
		if s.Fogged {
			unknown = true
			continue
		}
		if !s.Eligible || len(stage) >= stageLimit || !adjacentOpen(c) {
			continue
		}
		stage = append(stage, c)
		open[c] = true
	}
	return stage, remaining, unknown
}
