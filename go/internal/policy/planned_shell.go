package policy

import (
	"errors"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type Rectangle struct{ X, Z, Width, Height int32 }
type SiteCell struct {
	Cell                                                                   domain.Cell
	Walkable, Occupied, Zone, Roofed, Indoors, SupportsLight, StorageEmpty domain.Fact[bool]
	// Doorway reports a door, or a door blueprint or frame, on the cell;
	// indoor furnishing keeps the cells beside a doorway clear as its aisle.
	Doorway   domain.Fact[bool]
	Fertility domain.Fact[float64]
	Polluted  domain.Fact[bool]
	Glow      domain.Fact[float64]
	// Roof names the native roof def over the cell (rock roofs mark a
	// mountain face for excavation).
	Roof domain.Fact[string]
	// ZoneID names the native zone covering the cell when Zone is true.
	ZoneID domain.Fact[string]
	// Room names the native room holding the cell (#1224): growing-room
	// kinds pick a block per room interior.
	Room domain.Fact[string]
	// NaturalRock reports the cell's edifice is natural rock (#700): a
	// planned ring on it keeps it as wall; inside the room plan dig mines it
	// (#836).
	NaturalRock domain.Fact[bool]
	// Ruin reports an unowned edifice the player may deconstruct (#709).
	Ruin domain.Fact[bool]
	// PlayerEdifice names the definition of a player-owned edifice on the
	// cell, empty for none (#709): a ring of that wall kind stands on it.
	PlayerEdifice domain.Fact[string]
	// ClaimableRuin names the definition of a ruin the player may claim,
	// empty for none (#718): a ring of that wall kind claims it and keeps
	// it as wall instead of clearing it.
	ClaimableRuin domain.Fact[string]
	// RuinHold is the clearance census hold on the ruin covering the cell,
	// empty for none (#718): a ring claims a ruin only where the claim
	// would act on it (ShellRuinHolds).
	RuinHold string
}

// StarterRequest is one planned-room shell siting (#1231): the planned
// rooms of the builder's role, in plan order, and the ground they stand on.
type StarterRequest struct {
	Bounds Bounds
	Cells  []SiteCell
	// Protected contains accepted footprints and walkways.
	Protected []domain.Cell
	// WallDef is the ring's wall definition: a player wall of it already
	// standing on the ring is reused, and a claimable ruin of it claimed,
	// as wall (#709, #718). Empty reuses and claims none.
	WallDef string
	// Planned lists the layout plan's rooms for the builder's role (#787),
	// in plan order. The first buildable one is the only site.
	Planned []domain.RoomFootprint
}

type StarterLayout struct {
	// Room is the shell's bounding rectangle, walls included; Shell is its
	// exact geometry.
	Room, Storage Rectangle
	Shell         domain.RoomFootprint
	// Planned is the index of Shell in the request's Planned rooms.
	Planned int
	// Reused lists the ring cells natural rock or a player wall of the
	// ring's kind already walls (#700, #709); Claimed the ring cells holding
	// a claimable ruin wall of that kind, claimed and then kept as wall
	// (#718). Mined lists the interior and door cells of natural rock plan
	// dig mines before the ring (#836). The ring places walls on the rest.
	Reused, Mined, Claimed []domain.Cell
	// Blocked is, for each planned room that cannot stand, the first cell
	// stopping it and why (set even when no room can).
	Blocked []string
}

func sortedCells(set map[domain.Cell]bool) []domain.Cell {
	if len(set) == 0 {
		return nil
	}
	out := make([]domain.Cell, 0, len(set))
	for p := range set {
		out = append(out, p)
	}
	sort.Slice(out, func(i, j int) bool { return cellLess(out[i], out[j]) })
	return out
}

// starterStorage is the 3x3 indoor stockpile: the rectangle's upper-middle
// patch, or for any other shape the 3x3 nearest that position whose cells
// all lie in the interior.
func starterStorage(shell domain.RoomFootprint) Rectangle {
	b := shell.Bounds()
	inside := map[domain.Cell]bool{}
	for _, c := range shell.Interior() {
		inside[c] = true
	}
	fits := func(r Rectangle) bool {
		for _, p := range rectCells(r) {
			if !inside[p] {
				return false
			}
		}
		return true
	}
	want := Rectangle{b.X + b.Width/2 - 1, b.Z + b.Height/2 + 1, 3, 3}
	if fits(want) {
		return want
	}
	best, found := Rectangle{}, false
	for _, c := range shell.Interior() {
		r := Rectangle{c.X, c.Z, 3, 3}
		if !fits(r) {
			continue
		}
		if !found || squaredDistance(domain.Cell{X: r.X, Z: r.Z}, domain.Cell{X: want.X, Z: want.Z}) < squaredDistance(domain.Cell{X: best.X, Z: best.Z}, domain.Cell{X: want.X, Z: want.Z}) {
			best, found = r, true
		}
	}
	return best
}

func rectCells(r Rectangle) []domain.Cell {
	cells := make([]domain.Cell, 0, int(r.Width*r.Height))
	for x := r.X; x < r.X+r.Width; x++ {
		for z := r.Z; z < r.Z+r.Height; z++ {
			cells = append(cells, domain.Cell{X: x, Z: z})
		}
	}
	return cells
}
func cellLess(a, b domain.Cell) bool { return a.X < b.X || a.X == b.X && a.Z < b.Z }
func squaredDistance(a, b domain.Cell) int64 {
	x, z := int64(a.X)-int64(b.X), int64(a.Z)-int64(b.Z)
	return x*x + z*z
}

// PlannedLayout is the first planned room of the request that can stand
// now (#1231): every ring cell free lit ground, standing wall (natural rock,
// a player wall of the ring's kind) or a claimable ruin of that kind; every
// interior and door cell free lit ground or natural rock plan dig mines;
// its threshold open ground when observed. There is no free search: ok is
// false when no planned room is buildable or none is planned, and the
// caller refuses. These are proposals: native previews still decide
// legality. Missing cells are blocked.
func PlannedLayout(r StarterRequest) (layout StarterLayout, ok bool, err error) {
	if r.Bounds.Width <= 0 || r.Bounds.Height <= 0 || r.Bounds.Width > 4096 || r.Bounds.Height > 4096 {
		return StarterLayout{}, false, errors.New("invalid starter site bounds")
	}
	cells := make(map[domain.Cell]SiteCell, len(r.Cells))
	for _, c := range r.Cells {
		if _, exists := cells[c.Cell]; exists {
			return StarterLayout{}, false, errors.New("duplicate site cell")
		}
		cells[c.Cell] = c
	}
	protected := map[domain.Cell]bool{}
	for _, c := range r.Protected {
		protected[c] = true
	}
	unzoned := func(c SiteCell) bool { return positive(measured(c.Zone, func(v bool) bool { return !v })) }
	lit := func(p domain.Cell) bool {
		c, exists := cells[p]
		return exists && !protected[p] && positive(c.Walkable) && positive(measured(c.Occupied, func(v bool) bool { return !v })) && unzoned(c) && positive(c.SupportsLight)
	}
	rock := func(p domain.Cell) bool {
		c, exists := cells[p]
		return exists && !protected[p] && positive(c.NaturalRock) && unzoned(c)
	}
	wall := func(p domain.Cell) bool {
		if rock(p) {
			return true
		}
		c, exists := cells[p]
		def, known := c.PlayerEdifice.Value()
		return exists && known && r.WallDef != "" && def == r.WallDef && !protected[p] && unzoned(c)
	}
	claim := func(p domain.Cell) bool {
		c, exists := cells[p]
		def, known := c.ClaimableRuin.Value()
		return exists && known && r.WallDef != "" && def == r.WallDef && !protected[p] && !claimHold(c.RuinHold) && positive(c.Ruin) && unzoned(c)
	}
	var blocked []string
	why := func(p domain.Cell) string {
		c, exists := cells[p]
		switch {
		case !exists:
			return "not in the census"
		case protected[p]:
			return "protected"
		case !positive(c.Walkable):
			return "not walkable"
		case !positive(measured(c.Occupied, func(v bool) bool { return !v })):
			return "occupied"
		case !unzoned(c):
			return "zoned"
		case !positive(c.SupportsLight):
			return "cannot support light"
		}
		return "unknown"
	}
	for i, shell := range r.Planned {
		reused, claimed, mined := map[domain.Cell]bool{}, map[domain.Cell]bool{}, map[domain.Cell]bool{}
		buildable := true
		door := shell.Door()
		first := ""
		block := func(p domain.Cell, what string) {
			buildable = false
			if first == "" {
				first = fmt.Sprintf("%s (%d,%d) %s", what, p.X, p.Z, why(p))
			}
		}
		for _, p := range shell.Walls() {
			switch {
			case p == door:
				if rock(p) {
					mined[p] = true
				} else if !lit(p) {
					block(p, "door")
				}
			case wall(p):
				reused[p] = true
			case claim(p):
				claimed[p] = true
			case !lit(p):
				block(p, "wall")
			}
		}
		for _, p := range shell.Interior() {
			if rock(p) && positive(cells[p].SupportsLight) {
				mined[p] = true
			} else if !lit(p) {
				block(p, "interior")
			}
		}
		// The door opens onto the spine hallway, which the caller protects:
		// its threshold need only be open ground.
		if c, observed := cells[shell.Threshold()]; observed && !(positive(c.Walkable) && positive(measured(c.Occupied, func(v bool) bool { return !v }))) {
			block(shell.Threshold(), "threshold")
		}
		if !buildable {
			blocked = append(blocked, first)
			continue
		}
		b := shell.Bounds()
		return StarterLayout{Room: Rectangle{b.X, b.Z, b.Width, b.Height}, Storage: starterStorage(shell), Shell: shell, Planned: i, Reused: sortedCells(reused), Mined: sortedCells(mined), Claimed: sortedCells(claimed), Blocked: blocked}, true, nil
	}
	return StarterLayout{Blocked: blocked}, false, nil
}
