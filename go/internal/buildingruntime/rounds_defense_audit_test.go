package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

// defense/perimeter (run 36991368938, #1560): the killbox's fence bar runs
// wall to wall across the kill zone (#1544), so an access audit that held
// fences impassable cut the colonists off from the snake and every cell
// beyond the opening (all 8 colonists lost 2636 cells) and refused the
// firing line on every review. Raiders climb that bar, and so do
// colonists: inside the killbox, the defenders' doorway still reaches the
// entry over the audit's blocked cells.
func TestDefenseAuditKeepsTheKillboxPassable(t *testing.T) {
	t.Parallel()
	l := loadLayout(t, "testdata/defense/layout-perimeter-fence.json.gz", snapshot.LayoutPropose)
	layout, err := policy.DefenseLayouts(l.Request)
	if err != nil {
		t.Fatal(err)
	}
	walkable := map[domain.Cell]bool{}
	for _, c := range l.Request.Cells {
		if v, _ := c.Walkable.Value(); v {
			walkable[c.Cell] = true
		}
	}
	// The killbox's own placements bound the search, so no path runs
	// round its outer walls where the perimeter ring stands.
	lo, hi := layout.Entry, layout.Entry
	blocked := map[domain.Cell]bool{}
	var doors []domain.Cell
	fences := 0
	for _, tier := range layout.Tiers {
		for _, b := range tier.Buildings {
			c := b.Cell()
			lo.X, lo.Z, hi.X, hi.Z = min(lo.X, c.X), min(lo.Z, c.Z), max(hi.X, c.X), max(hi.Z, c.Z)
			switch b.Definition() {
			case defenseDefinitions.Door:
				doors = append(doors, c)
			case defenseDefinitions.Fence:
				fences++
			}
			if defenseAuditBlocks(b.Definition()) {
				blocked[c] = true
			}
		}
	}
	if len(doors) == 0 || fences == 0 {
		t.Fatalf("recorded killbox has %d doors and %d fences", len(doors), fences)
	}
	seen := map[domain.Cell]bool{doors[0]: true}
	queue := []domain.Cell{doors[0]}
	for len(queue) > 0 {
		c := queue[0]
		queue = queue[1:]
		if c == layout.Entry {
			return
		}
		for _, d := range []domain.Cell{{X: 1}, {X: -1}, {Z: 1}, {Z: -1}} {
			n := domain.Cell{X: c.X + d.X, Z: c.Z + d.Z}
			if n.X < lo.X || n.X > hi.X || n.Z < lo.Z || n.Z > hi.Z || seen[n] || blocked[n] || !walkable[n] {
				continue
			}
			seen[n] = true
			queue = append(queue, n)
		}
	}
	t.Fatalf("the defenders' doorway %v reaches no path to the entry %v through the killbox", doors[0], layout.Entry)
}
