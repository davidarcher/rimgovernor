package policy

import (
	"errors"
	"math"
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
	// starter shell reuses it as wall or mines it from the interior.
	NaturalRock domain.Fact[bool]
	// Ruin reports an unowned edifice the player may deconstruct (#709): a
	// starter ring on it clears it, then builds.
	Ruin domain.Fact[bool]
	// PlayerEdifice names the definition of a player-owned edifice on the
	// cell, empty for none (#709): a ring of that wall kind stands on it.
	PlayerEdifice domain.Fact[string]
	// ClaimableRuin names the definition of a ruin the player may claim,
	// empty for none (#718): a ring of that wall kind claims it and keeps
	// it as wall instead of clearing it.
	ClaimableRuin domain.Fact[string]
	// RuinHold is the clearance census hold on the ruin covering the cell,
	// empty for none (#718): the search counts a ruin cleared or claimed
	// only where the shelter-clear rung would act on it (ShellRuinHolds).
	RuinHold string
}

type StarterRequest struct {
	Bounds Bounds
	Anchor domain.Cell
	Cells  []SiteCell
	// Protected contains accepted footprints and walkways.
	Protected []domain.Cell
	// WallDef is the ring's wall definition: a player wall of it already
	// standing on the ring is reused as wall (#709). Empty reuses none.
	WallDef string
	// Size is the rectangle template's outer size; zero is the 9x9.
	Size int32
	// Planned lists the layout plan's rooms for the builder's role (#787),
	// in plan order. The first buildable one is the only site, exactly its
	// rectangle and door; the search runs only when none is buildable.
	Planned []domain.RoomFootprint
}
type StarterLayout struct {
	// Room is the shell's bounding rectangle, walls included; Shell is its
	// exact geometry, which for the rectangle style is Room's own perimeter.
	Room, Storage Rectangle
	Shell         domain.RoomFootprint
	Score         int64
	// Reused lists the ring cells natural rock already walls and Mined the
	// interior cells of natural rock the shell digs out (#700); the shell
	// places walls on the rest of its ring and nothing on Reused. Reused
	// also holds ring cells a player wall of the ring's kind stands on
	// (#709). Claimed lists the ring cells holding a claimable ruin wall of
	// that kind, claimed and then kept as wall (#718); Cleared the ring
	// cells holding any other ruin, deconstructed before the ring is built
	// there.
	Reused, Mined, Claimed, Cleared []domain.Cell
}

// shellReuseCredit is the score a ring cell of standing rock saves (one
// wall not built) and shellMineCost the score one interior rock cell
// costs to mine: in squared-distance units, so a rock-backed site beats
// open ground a few cells nearer, and mining costs more than building.
// shellMountainCost is the score one interior cell under overhead mountain
// adds: infestations spawn only under a thick rock roof, and that roof can
// never be removed, so a room clear of it wins when one is near.
// shellClearCost is the score one ring cell holding a ruin adds: the
// deconstruction is labour a wall on open ground does not need.
const (
	shellReuseCredit  = 6
	shellMineCost     = 8
	shellMountainCost = 4
	shellClearCost    = 2
)

// shellStanding is a shell's standing cells: the ring cells it reuses as
// wall, claims as wall, or clears of a ruin, and the interior cells it
// mines.
type shellStanding struct{ reused, claimed, cleared, mined map[domain.Cell]bool }

// kept reports a ring cell the shell places no wall on: reused or claimed.
func (s shellStanding) kept(p domain.Cell) bool { return s.reused[p] || s.claimed[p] }

func shellRock(shell domain.RoomFootprint, wall, claim, ruin, mineable func(domain.Cell) bool) shellStanding {
	s := shellStanding{map[domain.Cell]bool{}, map[domain.Cell]bool{}, map[domain.Cell]bool{}, map[domain.Cell]bool{}}
	reused, claimed, cleared, mined := s.reused, s.claimed, s.cleared, s.mined
	for _, p := range shell.Walls() {
		switch {
		case wall(p):
			reused[p] = true
		case claim(p):
			claimed[p] = true
		case ruin(p):
			cleared[p] = true
		}
	}
	for _, p := range shell.Interior() {
		if mineable(p) {
			mined[p] = true
		}
	}
	return s
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

// starterInterior is the rectangle template's interior cell count and the
// size an irregular footprint grows to when no template fits.
const starterInterior = 49

// ShellTemplate is one deterministic shell shape the starter search issues:
// a generator over a centre cell and the name acceptance tooling reports a
// sited shell under.
type ShellTemplate struct {
	Name  string
	Shape func(center domain.Cell) (domain.RoomFootprint, error)
}

// concaveTemplates are the composite shapes tried, in order, at every
// candidate centre once no 9x9 rectangle fits: an L of two
// three-wide arms (33 cells, 9x9 bounds) with its notch in each quadrant,
// which wraps a site's obstacle instead of giving up on a template, and two
// 4x4 chambers joined by a one-cell connector (35 cells, 13x6 or 6x13
// bounds), which spans two small clearings. Each is anchored on its
// bounding box's centre and faces south.
var concaveTemplates = []ShellTemplate{
	{Name: "concave-l-ne", Shape: func(c domain.Cell) (domain.RoomFootprint, error) {
		return domain.UnionFootprint([]domain.InteriorRect{{X: c.X - 3, Z: c.Z - 3, Width: 7, Height: 3}, {X: c.X - 3, Z: c.Z - 3, Width: 3, Height: 7}}, domain.South)
	}},
	{Name: "concave-l-nw", Shape: func(c domain.Cell) (domain.RoomFootprint, error) {
		return domain.UnionFootprint([]domain.InteriorRect{{X: c.X - 3, Z: c.Z - 3, Width: 7, Height: 3}, {X: c.X + 1, Z: c.Z - 3, Width: 3, Height: 7}}, domain.South)
	}},
	{Name: "concave-l-se", Shape: func(c domain.Cell) (domain.RoomFootprint, error) {
		return domain.UnionFootprint([]domain.InteriorRect{{X: c.X - 3, Z: c.Z + 1, Width: 7, Height: 3}, {X: c.X - 3, Z: c.Z - 3, Width: 3, Height: 7}}, domain.South)
	}},
	{Name: "concave-l-sw", Shape: func(c domain.Cell) (domain.RoomFootprint, error) {
		return domain.UnionFootprint([]domain.InteriorRect{{X: c.X - 3, Z: c.Z + 1, Width: 7, Height: 3}, {X: c.X + 1, Z: c.Z - 3, Width: 3, Height: 7}}, domain.South)
	}},
	{Name: "connector-ew", Shape: func(c domain.Cell) (domain.RoomFootprint, error) {
		return domain.UnionFootprint([]domain.InteriorRect{{X: c.X - 5, Z: c.Z - 2, Width: 4, Height: 4}, {X: c.X - 1, Z: c.Z, Width: 3, Height: 1}, {X: c.X + 2, Z: c.Z - 2, Width: 4, Height: 4}}, domain.South)
	}},
	{Name: "connector-ns", Shape: func(c domain.Cell) (domain.RoomFootprint, error) {
		return domain.UnionFootprint([]domain.InteriorRect{{X: c.X - 2, Z: c.Z - 5, Width: 4, Height: 4}, {X: c.X, Z: c.Z - 1, Width: 1, Height: 3}, {X: c.X - 2, Z: c.Z + 2, Width: 4, Height: 4}}, domain.South)
	}},
}

// ShellTemplates lists every template shape the starter search can issue
// besides the 9x9 rectangle: the concave shapes, in search order.
func ShellTemplates() []ShellTemplate {
	return concaveTemplates
}

// starterStorage is the 3x3 indoor stockpile: the rectangle template's
// fixed upper-middle patch, or for any other shape the 3x3 nearest that
// position whose cells all lie in the interior.
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

// starterSite is one shell the starter search found buildable.
type starterSite struct {
	tier  int
	score int64
	shell domain.RoomFootprint
}

// shellYard is the three-row yard beyond a shell's entrance side, one row
// out from the wall, that the site score wants clear.
func shellYard(shell domain.RoomFootprint) Rectangle {
	b := shell.Bounds()
	switch shell.Entrance() {
	case domain.North:
		return Rectangle{b.X, b.Z + b.Height + 1, b.Width, 3}
	case domain.East:
		return Rectangle{b.X + b.Width + 1, b.Z, 3, b.Height}
	case domain.West:
		return Rectangle{b.X - 4, b.Z, 3, b.Height}
	}
	return Rectangle{b.X, b.Z - 4, b.Width, 3}
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

// StarterLayouts ports the bounded 9x9 starter template ranking; fields are
// the layout plan's field blocks, not the template's (#1226). These are proposals: native placement/access previews still decide
// legality, and only admitted actions reserve geometry. Missing cells are blocked.
func StarterLayouts(r StarterRequest) ([]StarterLayout, error) {
	if r.Bounds.Width <= 0 || r.Bounds.Height <= 0 || r.Bounds.Width > 4096 || r.Bounds.Height > 4096 {
		return nil, errors.New("invalid starter site bounds")
	}
	inBounds := func(c domain.Cell) bool { return c.X >= 0 && c.Z >= 0 && c.X < r.Bounds.Width && c.Z < r.Bounds.Height }
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
		if f, k := c.Fertility.Value(); k && (math.IsNaN(f) || math.IsInf(f, 0) || f < 0) {
			return nil, errors.New("invalid fertility")
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
		return exists && !protected[p] && positive(c.Walkable) && positive(measured(c.Occupied, func(v bool) bool { return !v })) && positive(measured(c.Zone, func(v bool) bool { return !v }))
	}
	// A site's tier is its template's place in the search order: the
	// search prefers an earlier template a few cells further out over a
	// later one at the anchor, so a narrow site never trades the L for a
	// connector that happens to fit nearer.
	type site = starterSite
	// rock is unzoned, unprotected natural rock (#700): on the ring it
	// stands as the wall already, inside the room it is mined out where
	// it supports light, under a thick roof as under a thin one; the
	// native support check refuses a cut that would collapse the roof.
	rock := func(p domain.Cell) bool {
		c, exists := cells[p]
		return exists && !protected[p] && positive(c.NaturalRock) && positive(measured(c.Zone, func(v bool) bool { return !v }))
	}
	mineable := func(p domain.Cell) bool {
		return rock(p) && positive(cells[p].SupportsLight)
	}
	// wall is standing ring wall: rock, or a player wall of the ring's own
	// kind (#709). ruin is an unowned edifice the ring clears and then
	// builds on, so its ground must bear the wall.
	wall := func(p domain.Cell) bool {
		if rock(p) {
			return true
		}
		c, exists := cells[p]
		def, known := c.PlayerEdifice.Value()
		return exists && known && r.WallDef != "" && def == r.WallDef && !protected[p] && positive(measured(c.Zone, func(v bool) bool { return !v }))
	}
	ruin := func(p domain.Cell) bool {
		c, exists := cells[p]
		return exists && !protected[p] && c.RuinHold == "" && positive(c.Ruin) && positive(c.SupportsLight) && positive(measured(c.Zone, func(v bool) bool { return !v }))
	}
	// claim is a ruin wall of the ring's kind the player may claim (#718):
	// kept as wall, so its ground need bear nothing new.
	claim := func(p domain.Cell) bool {
		c, exists := cells[p]
		def, known := c.ClaimableRuin.Value()
		return exists && known && r.WallDef != "" && def == r.WallDef && !protected[p] && !claimHold(c.RuinHold) && positive(c.Ruin) && positive(measured(c.Zone, func(v bool) bool { return !v }))
	}
	size := r.Size
	if size == 0 {
		size = 9
	}
	if size < 3 {
		return nil, errors.New("invalid starter rectangle size")
	}
	// A shell is buildable when every cell of it is free, lit ground, rock
	// its ring reuses or rock its interior mines, its door stands on free
	// ground, and its door does not open onto ground observed blocked: a
	// door against rock or water seals the room as surely as a wall (a
	// template pressed against a corridor's rock row). A threshold beyond
	// the observed site window is not held against the shell; the shell's
	// own cells are.
	buildable := func(shell domain.RoomFootprint) bool {
		standing := shellRock(shell, wall, claim, ruin, mineable)
		for _, p := range shell.Cells() {
			if !standing.kept(p) && !standing.cleared[p] && !standing.mined[p] && (!free(p) || !positive(cells[p].SupportsLight)) {
				return false
			}
		}
		if standing.kept(shell.Door()) || standing.cleared[shell.Door()] {
			return false
		}
		_, observed := cells[shell.Threshold()]
		return !observed || free(shell.Threshold())
	}
	// A site scores by its centre's distance to the anchor plus three per
	// blocked cell in the three-row yard beyond the shell's entrance side,
	// less the wall work rock on its ring saves, plus the labour of
	// mining rock out of its interior and the infestation risk of each
	// interior cell under overhead mountain (#700).
	score := func(shell domain.RoomFootprint) int64 {
		b := shell.Bounds()
		score := squaredDistance(domain.Cell{X: b.X + b.Width/2, Z: b.Z + b.Height/2}, r.Anchor)
		standing := shellRock(shell, wall, claim, ruin, mineable)
		score += int64(len(standing.mined))*shellMineCost - int64(len(standing.reused)+len(standing.claimed))*shellReuseCredit + int64(len(standing.cleared))*shellClearCost
		for _, p := range shell.Interior() {
			if roof, _ := cells[p].Roof.Value(); roof == "RoofRockThick" {
				score += shellMountainCost
			}
		}
		for _, p := range rectCells(shellYard(shell)) {
			if !free(p) {
				score += 3
			}
		}
		return score
	}
	var sites []site
	// A planned room's door opens onto the spine hallway, which the caller
	// protects: its threshold need only be open ground. A wall a neighbour
	// already raised on a shared side is reused as ring (WallDef).
	plannedBuildable := func(shell domain.RoomFootprint) bool {
		standing := shellRock(shell, wall, claim, ruin, mineable)
		for _, p := range shell.Cells() {
			if !standing.kept(p) && !standing.cleared[p] && !standing.mined[p] && (!free(p) || !positive(cells[p].SupportsLight)) {
				return false
			}
		}
		if standing.kept(shell.Door()) || standing.cleared[shell.Door()] {
			return false
		}
		c, observed := cells[shell.Threshold()]
		return !observed || positive(c.Walkable) && positive(measured(c.Occupied, func(v bool) bool { return !v }))
	}
	for _, shell := range r.Planned {
		if plannedBuildable(shell) {
			sites = append(sites, site{0, score(shell), shell})
			break
		}
	}
	templated := func(templates []ShellTemplate) {
		for _, c := range ordered {
			for tier, template := range templates {
				shell, err := template.Shape(c)
				if err != nil || !buildable(shell) {
					continue
				}
				sites = append(sites, site{tier, score(shell), shell})
				break
			}
		}
	}
	if len(sites) == 0 {
		for _, c := range ordered {
			if c.X+size > r.Bounds.Width || c.Z+size > r.Bounds.Height {
				continue
			}
			shell, err := domain.RectangleFootprint(domain.RoomBounds{X: c.X, Z: c.Z, Width: size, Height: size}, domain.South)
			if err != nil || !buildable(shell) {
				continue
			}
			sites = append(sites, site{0, score(shell), shell})
		}
	}
	if len(sites) == 0 {
		// The rectangle does not fit: a concave template wraps
		// the obstacle or spans two clearings before the search resorts to
		// growing a shapeless footprint.
		templated(concaveTemplates)
	}
	if len(sites) == 0 {
		// Constrained terrain: grow one connected footprint from the free cell
		// nearest the anchor instead of giving up on a shelter.
		seeds := append([]domain.Cell(nil), ordered...)
		sort.Slice(seeds, func(i, j int) bool {
			a, b := squaredDistance(seeds[i], r.Anchor), squaredDistance(seeds[j], r.Anchor)
			if a != b {
				return a < b
			}
			return cellLess(seeds[i], seeds[j])
		})
		lit := func(p domain.Cell) bool { return free(p) && positive(cells[p].SupportsLight) }
		for _, seed := range seeds {
			if !lit(seed) {
				continue
			}
			if shell, ok := domain.GrowFootprint(seed, lit, starterInterior); ok {
				sites = append(sites, site{0, score(shell), shell})
				break
			}
		}
	}
	sort.Slice(sites, func(i, j int) bool {
		if sites[i].tier != sites[j].tier {
			return sites[i].tier < sites[j].tier
		}
		if sites[i].score != sites[j].score {
			return sites[i].score < sites[j].score
		}
		a, b := sites[i].shell.Bounds(), sites[j].shell.Bounds()
		return cellLess(domain.Cell{X: a.X, Z: a.Z}, domain.Cell{X: b.X, Z: b.Z})
	})
	if len(sites) > 24 {
		sites = sites[:24]
	}
	var layouts []StarterLayout
	for _, site := range sites {
		b := site.shell.Bounds()
		room := Rectangle{b.X, b.Z, b.Width, b.Height}
		layout := StarterLayout{Room: room, Storage: starterStorage(site.shell), Shell: site.shell, Score: site.score}
		standing := shellRock(site.shell, wall, claim, ruin, mineable)
		layout.Reused, layout.Claimed, layout.Cleared, layout.Mined = sortedCells(standing.reused), sortedCells(standing.claimed), sortedCells(standing.cleared), sortedCells(standing.mined)
		layouts = append(layouts, layout)
	}
	sort.Slice(layouts, func(i, j int) bool {
		a, b := layouts[i], layouts[j]
		if a.Score != b.Score {
			return a.Score < b.Score
		}
		return cellLess(domain.Cell{X: a.Room.X, Z: a.Room.Z}, domain.Cell{X: b.Room.X, Z: b.Room.Z})
	})
	if len(layouts) > 12 {
		layouts = layouts[:12]
	}
	return layouts, nil
}

// ShellShapesAtDoor returns every starter template shape whose south door
// would stand on door: the 9x9
// rectangle, then the concave templates. A shell planner uses it to
// recognise a shell it began earlier from the door still standing natively,
// so a restart reissues only the cells that shell is missing instead of
// siting a second one. These are the fallback behind the planner's own
// journal of earlier shell plans, which also recognises grown irregular
// shells that have no template.
func ShellShapesAtDoor(door domain.Cell) []domain.RoomFootprint {
	var shells []domain.RoomFootprint
	// A south door sits below the interior within a few cells of the
	// centre column (off it where a diagonal ring is two cells thick, or on
	// a connector room's near chamber), so every centre within the shape's
	// reach of the door is tried; the first centre reproducing the door is
	// the template's one placement there.
	at := func(template ShellTemplate, reach int32) {
		for dz := int32(2); dz <= reach; dz++ {
			for dx := -reach; dx <= reach; dx++ {
				shell, err := template.Shape(domain.Cell{X: door.X + dx, Z: door.Z + dz})
				if err == nil && shell.Door() == door {
					shells = append(shells, shell)
					return
				}
			}
		}
	}
	shell, err := domain.RectangleFootprint(domain.RoomBounds{X: door.X - 4, Z: door.Z, Width: 9, Height: 9}, domain.South)
	if err == nil && shell.Door() == door {
		shells = append(shells, shell)
	}
	for _, template := range concaveTemplates {
		at(template, 7)
	}
	return shells
}
