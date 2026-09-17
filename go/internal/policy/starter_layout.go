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
	// Roof names the native roof def over the cell (rock roofs mark a
	// mountain face for excavation).
	Roof domain.Fact[string]
	// ZoneID names the native zone covering the cell when Zone is true.
	ZoneID domain.Fact[string]
}

// ShelterStyle selects the starter shell's shape family. The rectangle is
// the 9x9 template; the hut style prefers the circular and oval templates a
// neolithic colony builds and falls back to the rectangle when none fits.
type ShelterStyle string

const (
	ShelterRectangle ShelterStyle = "rectangle"
	ShelterHut       ShelterStyle = "hut"
)

type StarterRequest struct {
	Bounds Bounds
	Anchor domain.Cell
	Cells  []SiteCell
	// Protected contains accepted footprints, player exclusions and walkways.
	Protected                                                     []domain.Cell
	NutritionPerDay, CropGrowDays, HarvestNutrition, FertilityMin domain.Fact[float64]
	// Shelter is the preferred shape family; empty means the rectangle.
	Shelter ShelterStyle
	// Crop, when known, replaces the bare crop facts above for farm scoring.
	Crop domain.Fact[CropChoice]
	// Zones lists existing growing zones so farms can extend managed ones.
	Zones []FarmZone
}
type StarterLayout struct {
	// Room is the shell's bounding rectangle, walls included; Shell is its
	// exact geometry, which for the rectangle style is Room's own perimeter.
	Room, Storage Rectangle
	Shell         domain.RoomFootprint
	Farms         []Rectangle
	FarmSites     FarmSitePlan
	Score         int64
	SelectedCells int
	TargetCells   domain.Fact[int]
}

// starterInterior is the rectangle template's interior cell count and the
// size an irregular footprint grows to when no template fits.
const starterInterior = 49

// hutTemplates are tried in order at every candidate centre; the first that
// fits is that centre's hut. Radii keep every interior cell within roof
// support so the finished hut roofs itself.
type hutTemplate struct {
	radiusX, radiusZ int32
	orientation      domain.EllipseOrientation
}

var hutTemplates = []hutTemplate{
	{4, 4, domain.EllipseNorthSouth},
	{3, 5, domain.EllipseNorthSouth},
	{3, 5, domain.EllipseEastWest},
	{3, 5, domain.EllipseNorthEast},
	{3, 5, domain.EllipseNorthWest},
	{3, 3, domain.EllipseNorthSouth},
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

// StarterLayouts ports the bounded 9x9 starter template and fragmented-field
// ranking. These are proposals: native placement/access previews still decide
// legality, and only admitted actions reserve geometry. Missing cells are blocked.
func StarterLayouts(r StarterRequest) ([]StarterLayout, error) {
	if r.Bounds.Width <= 0 || r.Bounds.Height <= 0 || r.Bounds.Width > 4096 || r.Bounds.Height > 4096 || len(r.Cells) > 65536 || len(r.Protected) > 65536 {
		return nil, errors.New("invalid starter site bounds")
	}
	inBounds := func(c domain.Cell) bool { return c.X >= 0 && c.Z >= 0 && c.X < r.Bounds.Width && c.Z < r.Bounds.Height }
	if !inBounds(r.Anchor) {
		return nil, errors.New("invalid colony anchor")
	}
	for _, f := range []domain.Fact[float64]{r.NutritionPerDay, r.CropGrowDays, r.HarvestNutrition, r.FertilityMin} {
		if v, k := f.Value(); k && (math.IsNaN(v) || math.IsInf(v, 0) || v < 0) {
			return nil, errors.New("invalid crop fact")
		}
	}
	n, nk := r.NutritionPerDay.Value()
	days, dk := r.CropGrowDays.Value()
	yield, yk := r.HarvestNutrition.Value()
	target := 0
	targetFact := domain.Unknown[int]()
	if nk && dk && yk && days > 0 && yield > 0 {
		v := math.Ceil(n * days * 2.5 / yield)
		if math.IsInf(v, 0) || v > 65536 {
			return nil, errors.New("crop target exceeds bounded site search")
		}
		target = int(v)
		targetFact = domain.Known(target)
	}
	crop, ck := r.Crop.Value()
	if !ck {
		crop = CropChoice{GrowDays: r.CropGrowDays, HarvestNutrition: r.HarvestNutrition, FertilityMin: r.FertilityMin, FertilitySensitivity: domain.Known(1.0)}
	}
	_, fk := crop.FertilityMin.Value()
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
	type site struct {
		score int64
		shell domain.RoomFootprint
	}
	buildable := func(shell domain.RoomFootprint) bool {
		for _, p := range shell.Cells() {
			if !free(p) || !positive(cells[p].SupportsLight) {
				return false
			}
		}
		return true
	}
	// A site scores by its centre's distance to the anchor plus three per
	// blocked cell in the three-row yard south of the shell.
	score := func(shell domain.RoomFootprint) int64 {
		b := shell.Bounds()
		score := squaredDistance(domain.Cell{X: b.X + b.Width/2, Z: b.Z + b.Height/2}, r.Anchor)
		for _, p := range rectCells(Rectangle{b.X, b.Z - 4, b.Width, 3}) {
			if !free(p) {
				score += 3
			}
		}
		return score
	}
	var sites []site
	if r.Shelter == ShelterHut {
		for _, c := range ordered {
			for _, template := range hutTemplates {
				shell, err := domain.EllipseFootprint(c, template.radiusX, template.radiusZ, template.orientation, domain.South)
				if err != nil || !buildable(shell) {
					continue
				}
				sites = append(sites, site{score(shell), shell})
				break
			}
		}
	}
	if len(sites) == 0 {
		for _, c := range ordered {
			if c.X+9 > r.Bounds.Width || c.Z+9 > r.Bounds.Height {
				continue
			}
			shell, err := domain.RectangleFootprint(domain.RoomBounds{X: c.X, Z: c.Z, Width: 9, Height: 9}, domain.South)
			if err != nil || !buildable(shell) {
				continue
			}
			sites = append(sites, site{score(shell), shell})
		}
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
				sites = append(sites, site{score(shell), shell})
				break
			}
		}
	}
	sort.Slice(sites, func(i, j int) bool {
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
		x, z, room := b.X, b.Z, Rectangle{b.X, b.Z, b.Width, b.Height}
		reserved := map[domain.Cell]bool{}
		for _, r := range []Rectangle{{x - 1, z - 1, room.Width + 2, room.Height + 2}, {x, z - 5, room.Width, 4}} {
			for _, p := range rectCells(r) {
				reserved[p] = true
			}
		}
		storage := starterStorage(site.shell)
		layout := StarterLayout{Room: room, Storage: storage, Shell: site.shell, Score: site.score, TargetCells: targetFact}
		chosen := 0
		if fk && target > 0 {
			protectedCells := append([]domain.Cell(nil), r.Protected...)
			for p := range reserved {
				protectedCells = append(protectedCells, p)
			}
			farms := PlanFarmSites(FarmSiteRequest{Bounds: r.Bounds, Anchor: domain.Cell{X: x + room.Width/2, Z: z + room.Height/2}, Storage: domain.Known(domain.Cell{X: storage.X + storage.Width/2, Z: storage.Z + storage.Height/2}), Cells: r.Cells, Protected: protectedCells, Zones: r.Zones, Crop: crop, Needed: target})
			layout.Farms = farms.Patches
			layout.FarmSites = farms
			chosen = farms.Cells
		}
		layout.SelectedCells = chosen
		layout.Score += int64(max(0, target-chosen)) * 2
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

// HutTemplateShells returns every hut template centred on c with a south
// entrance, in the order the starter search tries them, so acceptance tooling
// can recognise a native shell as one of the shapes this policy issues.
func HutTemplateShells(c domain.Cell) []domain.RoomFootprint {
	var shells []domain.RoomFootprint
	for _, template := range hutTemplates {
		shell, err := domain.EllipseFootprint(c, template.radiusX, template.radiusZ, template.orientation, domain.South)
		if err == nil {
			shells = append(shells, shell)
		}
	}
	return shells
}

// ShellShapesAtDoor returns every starter shell shape whose south door would
// stand on door: the hut templates for the hut style, then the 9x9
// rectangle. A shell planner uses it to recognise a shell it began earlier
// from the door still standing natively, so a restart reissues only the
// cells that shell is missing instead of siting a second one. These are the
// fallback behind the planner's own journal of earlier shell plans, which
// also recognises grown irregular shells that have no template.
func ShellShapesAtDoor(door domain.Cell, style ShelterStyle) []domain.RoomFootprint {
	var shells []domain.RoomFootprint
	if style == ShelterHut {
		for _, template := range hutTemplates {
			// A south door sits just below the interior, near the centre
			// column but off it where a diagonal ring is two cells thick, so
			// every centre within the longer radius of the door is tried.
			reach := max(template.radiusX, template.radiusZ) + 1
			found := false
			for dz := int32(2); dz <= reach && !found; dz++ {
				for dx := int32(-2); dx <= 2 && !found; dx++ {
					center := domain.Cell{X: door.X + dx, Z: door.Z + dz}
					shell, err := domain.EllipseFootprint(center, template.radiusX, template.radiusZ, template.orientation, domain.South)
					if err == nil && shell.Door() == door {
						shells = append(shells, shell)
						found = true
					}
				}
			}
		}
	}
	shell, err := domain.RectangleFootprint(domain.RoomBounds{X: door.X - 4, Z: door.Z, Width: 9, Height: 9}, domain.South)
	if err == nil && shell.Door() == door {
		shells = append(shells, shell)
	}
	return shells
}
