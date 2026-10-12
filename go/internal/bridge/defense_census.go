package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// frameDefPrefix is the def name the game gives a frame (ThingDefGenerator_Buildings):
// the mirror lists a frame as the building it will become, the census as
// its own def, so a tier never reads as built while only its frame stands.
const frameDefPrefix = "Frame_"

// frameFill is a frame's fillPercent (ThingDefGenerator_Buildings.NewFrameDef_Thing);
// a blueprint has none.
const frameFill = 0.2

// coverFloor is the least fillPercent CoverGrid registers.
const coverFloor = 0.001

// DefenseSiteFromCells is the defense census of region read off the cell
// mirror (the planning window) and the definition catalog, the facts
// observations_read_defense_site answered natively: the edifice and terrain
// on each cell, the cell's cover (the thing of highest fillPercent, as the
// game's CoverGrid keeps it) with the designator that removes it, whether a
// player door stands, and EdgeReachable as a flood over the mirror from the
// map edge (see edgeReachable). A cell the mirror does not hold, or holds
// without its terrain or walkability, is fogged: unknown.
//
// What the mirror does not carry is left empty: EdificeStuff (a wooden door
// upgraded to plasteel is the waiting fight's, which still reads the native
// census). The returned site has no Context: it is the projection's own tick.
func DefenseSiteFromCells(cells []policy.SiteCell, bounds policy.Bounds, region CellRect, catalog *DefinitionCatalog) (DefenseSite, error) {
	if catalog == nil {
		return DefenseSite{}, contract("defense census needs the definition catalog")
	}
	if bounds.Width < 1 || bounds.Height < 1 {
		return DefenseSite{}, contract("defense census needs the map bounds")
	}
	if err := validateDefenseRegion(region); err != nil || region.Max.X >= bounds.Width || region.Max.Z >= bounds.Height {
		return DefenseSite{}, contract("invalid defense site region")
	}
	width, height := int(bounds.Width), int(bounds.Height)
	grid := make([]*policy.SiteCell, width*height)
	for i := range cells {
		c := cells[i].Cell
		if c.X >= 0 && c.X < bounds.Width && c.Z >= 0 && c.Z < bounds.Height {
			grid[int(c.Z)*width+int(c.X)] = &cells[i]
		}
	}
	census := &censusReader{catalog: catalog, chunks: map[string]bool{}}
	reach := census.edgeReachable(grid, width, height)
	site := DefenseSite{Width: uint32(bounds.Width), Height: uint32(bounds.Height), Region: region}
	site.Cells = make([]DefenseCell, 0, region.Cells())
	for z := region.Min.Z; z <= region.Max.Z; z++ {
		for x := region.Min.X; x <= region.Max.X; x++ {
			i := int(z)*width + int(x)
			row, err := census.cell(grid[i], domain.Cell{X: x, Z: z})
			if err != nil {
				return DefenseSite{}, err
			}
			row.EdgeReachable = reach[i]
			site.Cells = append(site.Cells, row)
		}
	}
	return site, nil
}

// censusReader decodes mirror cells against the catalog; chunks caches the
// per-def "is a stone chunk" answer.
type censusReader struct {
	catalog *DefinitionCatalog
	chunks  map[string]bool
}

// censusCell is a mirror cell's traversal as the census and its flood read it.
type censusCell struct {
	known              bool
	walkable, passable bool
	door               bool
}

// read decodes the traversal facts of one mirror cell. A held cell with an
// unknown walkability or terrain is not known.
func (r *censusReader) read(cell *policy.SiteCell) censusCell {
	if cell == nil {
		return censusCell{}
	}
	walkable, walkKnown := cell.Walkable.Value()
	terrain, terrainKnown := cell.Terrain.Value()
	if !walkKnown || !terrainKnown || validID(terrain) != nil {
		return censusCell{}
	}
	out := censusCell{known: true, passable: true}
	for _, t := range cell.Things {
		if t.Has(policy.FlagImpassable) {
			out.passable = false
		}
		if t.Has(policy.FlagEdifice) && !t.Has(policy.FlagFrame) && t.Category == policy.ThingBuilding {
			if doorway, _ := cell.Doorway.Value(); doorway {
				out.door = true
			}
		}
	}
	out.walkable = walkable && out.passable
	return out
}

// edgeReachable is the native "can reach a map edge without opening doors"
// (Reachability.CanReachMapEdge with NoPassClosedDoors): a flood over
// walkable cells from the walkable cells on the map edge, 4-connected as the
// game's region links are, a door read closed. A cell the mirror does not
// know blocks it: fog hides mountain far more often than open ground (the
// game unfogs every outdoor region connected to the start), so an unknown
// cell is never proof of a route. The mirror is the whole map; a smaller
// window reaches only the edge cells it holds.
func (r *censusReader) edgeReachable(grid []*policy.SiteCell, width, height int) []bool {
	open := make([]bool, len(grid))
	for i, cell := range grid {
		if c := r.read(cell); c.known && c.walkable && !c.door {
			open[i] = true
		}
	}
	reach := make([]bool, len(grid))
	var queue []int
	push := func(i int) {
		if open[i] && !reach[i] {
			reach[i] = true
			queue = append(queue, i)
		}
	}
	for x := 0; x < width; x++ {
		push(x)
		push((height-1)*width + x)
	}
	for z := 0; z < height; z++ {
		push(z * width)
		push(z*width + width - 1)
	}
	for head := 0; head < len(queue); head++ {
		i := queue[head]
		x, z := i%width, i/width
		if x > 0 {
			push(i - 1)
		}
		if x < width-1 {
			push(i + 1)
		}
		if z > 0 {
			push(i - width)
		}
		if z < height-1 {
			push(i + width)
		}
	}
	return reach
}

// cell is one census row: fogged when the mirror does not know the cell.
func (r *censusReader) cell(cell *policy.SiteCell, at domain.Cell) (DefenseCell, error) {
	view := r.read(cell)
	if !view.known {
		return DefenseCell{Cell: at, Fogged: true}, nil
	}
	terrain, _ := cell.Terrain.Value()
	out := DefenseCell{Cell: at, Terrain: terrain, Walkable: view.walkable, Passable: view.passable, Door: view.door}
	out.Roofed, _ = cell.Roofed.Value()
	out.Unbridging, _ = cell.FoundationRemoval.Value()
	var edifice *policy.Thing
	var cover *policy.Thing
	fill := coverFloor
	for i := range cell.Things {
		t := &cell.Things[i]
		if t.Has(policy.FlagEdifice) {
			edifice = t
		}
		if f := r.fill(t); f > fill {
			cover, fill = t, f
		}
	}
	if edifice != nil {
		out.EdificeDefName = edificeName(*edifice)
		out.PlayerOwned = edifice.Faction == policy.FactionPlayer
		out.NaturalRock = edifice.Has(policy.FlagNaturalRock) || r.catalog.ThingDef(edifice.Def).GetMineable()
	}
	if cover != nil {
		out.CoverFill = min(fill, 1)
		kind, err := r.coverKind(*cover)
		if err != nil {
			return DefenseCell{}, err
		}
		if kind != o.CoverKind_COVER_KIND_UNSPECIFIED {
			out.Cover = &DefenseCover{ThingID: thingLoadID(*cover), DefName: edificeName(*cover), Kind: kind,
				Designated: cover.Has(policy.FlagDesignated)}
		}
	}
	return out, nil
}

// edificeName is the def the game names the thing by: a frame is its own def.
func edificeName(t policy.Thing) string {
	if t.Has(policy.FlagFrame) {
		return frameDefPrefix + t.Def
	}
	return t.Def
}

// thingLoadID is the game's Thing.GetUniqueLoadID of a listed thing.
func thingLoadID(t policy.Thing) string {
	t.Def = edificeName(t)
	return t.LoadID()
}

// fill is the fillPercent CoverGrid sees on a thing: a frame's own, none for
// a blueprint, else its def's.
func (r *censusReader) fill(t *policy.Thing) float64 {
	switch {
	case t.Has(policy.FlagBlueprint):
		return 0
	case t.Has(policy.FlagFrame):
		return frameFill
	}
	return float64(r.catalog.ThingDef(t.Def).GetFillPercent())
}

// coverKind is NativeClearCover.KindOf: how the game's designators remove
// the thing, unspecified for one they cannot (a corpse, filth).
func (r *censusReader) coverKind(t policy.Thing) (o.CoverKind, error) {
	switch {
	case t.Category == policy.ThingPlant:
		return o.CoverKind_COVER_KIND_PLANT, nil
	case t.Category == policy.ThingBuilding && (t.Has(policy.FlagNaturalRock) || r.catalog.ThingDef(t.Def).GetMineable()) && !t.Has(policy.FlagFrame) && !t.Has(policy.FlagBlueprint):
		return o.CoverKind_COVER_KIND_MINEABLE, nil
	case t.Category == policy.ThingItem:
		chunk, err := r.chunk(t.Def)
		if err != nil || !chunk {
			return o.CoverKind_COVER_KIND_UNSPECIFIED, err
		}
		return o.CoverKind_COVER_KIND_CHUNK, nil
	case t.Category == policy.ThingBuilding:
		return o.CoverKind_COVER_KIND_BUILDING, nil
	}
	return o.CoverKind_COVER_KIND_UNSPECIFIED, nil
}

// chunkCategory is the thing category the game's chunks sit within.
const chunkCategory = "Chunks"

func (r *censusReader) chunk(def string) (bool, error) {
	if known, ok := r.chunks[def]; ok {
		return known, nil
	}
	row := r.catalog.ThingDef(def)
	if row == nil {
		r.chunks[def] = false
		return false, nil
	}
	within, err := r.catalog.categoriesWithin(def, row)
	if err != nil {
		return false, err
	}
	r.chunks[def] = within[chunkCategory]
	return within[chunkCategory], nil
}
