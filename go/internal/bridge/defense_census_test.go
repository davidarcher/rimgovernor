package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// censusCatalog holds the defs the census cases read: a wall, a tree, a
// granite chunk under Chunks, a rock and a door.
func censusCatalog() *DefinitionCatalog {
	catalog := &DefinitionCatalog{ThingDefs: map[string]*d.ThingDef{
		"Wall":          {DefName: "Wall", FillPercent: 1},
		"Door":          {DefName: "Door", FillPercent: 1},
		"Plant_TreeOak": {DefName: "Plant_TreeOak", FillPercent: 0.25},
		"ChunkGranite":  {DefName: "ChunkGranite", FillPercent: 0.5, ThingCategories: []string{"StoneChunks"}},
		"Sandbags":      {DefName: "Sandbags", FillPercent: 0.55},
		"Granite":       {DefName: "Granite", FillPercent: 1, Mineable: true},
	}, Defs: map[protoreflect.FullName]map[string]proto.Message{}}
	categories := map[string]proto.Message{
		"StoneChunks": &d.ThingCategoryDef{DefName: "StoneChunks", Parent: "Chunks"},
		"Chunks":      &d.ThingCategoryDef{DefName: "Chunks", Parent: "Root"},
		"Root":        &d.ThingCategoryDef{DefName: "Root"},
	}
	catalog.Defs[(&d.ThingCategoryDef{}).ProtoReflect().Descriptor().FullName()] = categories
	return catalog
}

func testSiteCell(x, z int32, walkable bool, things ...policy.Thing) policy.SiteCell {
	return policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(walkable), Terrain: domain.Known("Soil"), Roofed: domain.Known(false),
		Doorway: domain.Known(false), FoundationRemoval: domain.Known(false), Things: things}
}

func edifice(def string, faction policy.ThingFaction, flags policy.ThingFlags) policy.Thing {
	return policy.Thing{Def: def, Category: policy.ThingBuilding, Faction: faction, Flags: policy.FlagEdifice | flags, ID: 3, Count: 1, Building: &policy.BuildingState{}}
}

// TestDefenseSiteFromCells reads each kind of cell the way the native
// census did: walls, frames, blueprints, cover, doors, fog and the flood.
func TestDefenseSiteFromCells(t *testing.T) {
	// A 6x3 map. Row 1: open, wall, open, tree, door(player), frame.
	// Row 0 and 2 are open ground except (5,0), unknown (fogged).
	var cells []policy.SiteCell
	for z := int32(0); z < 3; z++ {
		for x := int32(0); x < 6; x++ {
			if x == 5 && z == 0 {
				continue
			}
			cells = append(cells, testSiteCell(x, z, true))
		}
	}
	at := func(x, z int32) *policy.SiteCell {
		for i := range cells {
			if cells[i].Cell == (domain.Cell{X: x, Z: z}) {
				return &cells[i]
			}
		}
		t.Fatalf("no cell %d,%d", x, z)
		return nil
	}
	at(1, 1).Things = []policy.Thing{edifice("Wall", policy.FactionPlayer, policy.FlagImpassable|policy.FlagDeconstructible)}
	at(1, 1).Walkable = domain.Known(false)
	at(3, 1).Things = []policy.Thing{{Def: "Plant_TreeOak", Category: policy.ThingPlant, ID: 8, Count: 1}}
	at(4, 1).Things = []policy.Thing{edifice("Door", policy.FactionPlayer, 0)}
	at(4, 1).Doorway = domain.Known(true)
	at(5, 1).Things = []policy.Thing{edifice("Wall", policy.FactionPlayer, policy.FlagFrame)}
	at(2, 2).Things = []policy.Thing{{Def: "Sandbags", Category: policy.ThingBuilding, Flags: policy.FlagBlueprint, ID: 4, Count: 1}}
	at(0, 2).Things = []policy.Thing{{Def: "ChunkGranite", Category: policy.ThingItem, Flags: policy.FlagDesignated | policy.FlagHaulable, ID: 6, Count: 1}}
	at(3, 2).FoundationRemoval = domain.Known(true)
	at(2, 0).Terrain = domain.Unknown[string]()

	site, err := DefenseSiteFromCells(cells, policy.Bounds{Width: 6, Height: 3}, CellRect{Min: domain.Cell{}, Max: domain.Cell{X: 5, Z: 2}}, censusCatalog())
	if err != nil {
		t.Fatal(err)
	}
	if len(site.Cells) != 18 || site.Width != 6 || site.Height != 3 {
		t.Fatalf("site %dx%d with %d cells", site.Width, site.Height, len(site.Cells))
	}
	row := func(x, z int32) DefenseCell { return site.Cells[int(z)*6+int(x)] }
	if c := row(5, 0); !c.Fogged {
		t.Fatalf("an unheld cell is fogged: %+v", c)
	}
	if c := row(2, 0); !c.Fogged {
		t.Fatalf("a held cell without terrain is unknown: %+v", c)
	}
	wall := row(1, 1)
	if wall.EdificeDefName != "Wall" || !wall.PlayerOwned || wall.Passable || wall.Walkable || wall.CoverFill != 1 || wall.Cover == nil || wall.Cover.Kind != o.CoverKind_COVER_KIND_BUILDING || wall.Cover.ThingID != "Thing_Wall3" {
		t.Fatalf("wall %+v", wall)
	}
	if tree := row(3, 1); tree.CoverFill != 0.25 || tree.Cover == nil || tree.Cover.Kind != o.CoverKind_COVER_KIND_PLANT || !tree.Passable || tree.EdificeDefName != "" {
		t.Fatalf("tree %+v", tree)
	}
	if door := row(4, 1); !door.Door || !door.PlayerOwned || door.EdificeDefName != "Door" || !door.Walkable {
		t.Fatalf("door %+v", door)
	}
	if frame := row(5, 1); frame.EdificeDefName != "Frame_Wall" || frame.CoverFill != frameFill || frame.Door || frame.Cover == nil || frame.Cover.DefName != "Frame_Wall" || frame.Cover.ThingID != "Thing_Frame_Wall3" {
		t.Fatalf("frame is its own def, not the building it will be: %+v", frame)
	}
	if bp := row(2, 2); bp.EdificeDefName != "" || bp.CoverFill != 0 || bp.Cover != nil {
		t.Fatalf("a blueprint is neither edifice nor cover: %+v", bp)
	}
	if chunk := row(0, 2); chunk.Cover == nil || chunk.Cover.Kind != o.CoverKind_COVER_KIND_CHUNK || !chunk.Cover.Designated || chunk.CoverFill != 0.5 || chunk.Cover.Designation() != domain.CoverClearanceHaul {
		t.Fatalf("chunk %+v", chunk)
	}
	if c := row(3, 2); !c.Unbridging {
		t.Fatalf("foundation removal: %+v", c)
	}
	// The wall and door stand across row 1, and the tree and frame leave
	// row 1's far side open to rows 0 and 2: every known walkable cell
	// reaches an edge by rows 0 and 2, the closed door cell is a start region
	// the game answers true from, through its open sides (CanReachMapEdge
	// never tests the start region against the traverse mode).
	if !row(0, 1).EdgeReachable || !row(3, 1).EdgeReachable || !row(4, 1).EdgeReachable || row(1, 1).EdgeReachable {
		t.Fatalf("reach: open %v tree %v door %v wall %v", row(0, 1).EdgeReachable, row(3, 1).EdgeReachable, row(4, 1).EdgeReachable, row(1, 1).EdgeReachable)
	}
}

// TestDefenseSiteFromCellsSealed: a ring of wall around an inner cell leaves it
// unreachable from the edge, and a door in the ring does not open it.
func TestDefenseSiteFromCellsSealed(t *testing.T) {
	var cells []policy.SiteCell
	for z := int32(0); z < 5; z++ {
		for x := int32(0); x < 5; x++ {
			c := testSiteCell(x, z, true)
			ring := x >= 1 && x <= 3 && z >= 1 && z <= 3 && (x == 1 || x == 3 || z == 1 || z == 3)
			if ring {
				c.Walkable = domain.Known(false)
				c.Things = []policy.Thing{edifice("Wall", policy.FactionPlayer, policy.FlagImpassable)}
			}
			cells = append(cells, c)
		}
	}
	for i := range cells {
		if cells[i].Cell == (domain.Cell{X: 2, Z: 1}) {
			cells[i].Walkable, cells[i].Doorway = domain.Known(true), domain.Known(true)
			cells[i].Things = []policy.Thing{edifice("Door", policy.FactionPlayer, 0)}
		}
	}
	site, err := DefenseSiteFromCells(cells, policy.Bounds{Width: 5, Height: 5}, CellRect{Max: domain.Cell{X: 4, Z: 4}}, censusCatalog())
	if err != nil {
		t.Fatal(err)
	}
	for _, c := range site.Cells {
		inside := c.Cell == (domain.Cell{X: 2, Z: 2})
		if inside && c.EdgeReachable {
			t.Fatalf("the sealed cell is reachable through a closed door: %+v", c)
		}
		if c.Cell == (domain.Cell{X: 2, Z: 1}) && !c.EdgeReachable {
			t.Fatalf("the closed door's own cell reaches the edge by its outer side: %+v", c)
		}
		if c.Cell == (domain.Cell{X: 0, Z: 0}) && !c.EdgeReachable {
			t.Fatalf("the edge is not reachable: %+v", c)
		}
	}
}

// TestDefenseSiteFromCellsFloodsThroughFog: a pocket walled off from the edge
// on every side but one fogged cell reads reachable (raiders path the real
// map), while the fogged cell itself asserts nothing.
func TestDefenseSiteFromCellsFloodsThroughFog(t *testing.T) {
	var cells []policy.SiteCell
	for z := int32(0); z < 5; z++ {
		for x := int32(0); x < 5; x++ {
			if x == 2 && z == 1 {
				continue // fogged gap in the ring
			}
			c := testSiteCell(x, z, true)
			if x >= 1 && x <= 3 && z >= 1 && z <= 3 && (x == 1 || x == 3 || z == 1 || z == 3) {
				c.Walkable = domain.Known(false)
				c.Things = []policy.Thing{edifice("Wall", policy.FactionPlayer, policy.FlagImpassable)}
			}
			cells = append(cells, c)
		}
	}
	site, err := DefenseSiteFromCells(cells, policy.Bounds{Width: 5, Height: 5}, CellRect{Max: domain.Cell{X: 4, Z: 4}}, censusCatalog())
	if err != nil {
		t.Fatal(err)
	}
	if c := site.Cells[2*5+2]; !c.EdgeReachable {
		t.Fatalf("the pocket is reachable through the fogged gap: %+v", c)
	}
	if c := site.Cells[1*5+2]; !c.Fogged || c.EdgeReachable {
		t.Fatalf("a fogged cell asserts no reachability: %+v", c)
	}
}

// TestDefenseSiteFromCellsDoorBehindDoor: a closed door whose every side is
// sealed or another door does not reach the edge, though a door on the map
// edge does (CanReachMapEdge starts from the door's own region).
func TestDefenseSiteFromCellsDoorBehindDoor(t *testing.T) {
	var cells []policy.SiteCell
	for z := int32(0); z < 5; z++ {
		for x := int32(0); x < 5; x++ {
			c := testSiteCell(x, z, true)
			switch {
			case x == 2 && z <= 2:
				c.Doorway = domain.Known(true)
				c.Things = []policy.Thing{edifice("Door", policy.FactionPlayer, 0)}
			case x >= 1 && x <= 3 && z >= 1 && z <= 3:
				c.Walkable = domain.Known(false)
				c.Things = []policy.Thing{edifice("Wall", policy.FactionPlayer, policy.FlagImpassable)}
			}
			cells = append(cells, c)
		}
	}
	site, err := DefenseSiteFromCells(cells, policy.Bounds{Width: 5, Height: 5}, CellRect{Max: domain.Cell{X: 4, Z: 4}}, censusCatalog())
	if err != nil {
		t.Fatal(err)
	}
	reach := func(x, z int) bool { return site.Cells[z*5+x].EdgeReachable }
	if !reach(2, 0) {
		t.Fatal("a door on the map edge reaches it")
	}
	if reach(2, 1) || reach(2, 2) {
		t.Fatalf("doors behind a door reach nothing: %v %v", reach(2, 1), reach(2, 2))
	}
}
