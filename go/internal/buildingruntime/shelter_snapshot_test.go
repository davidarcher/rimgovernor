package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

// The shelter siting snapshots replace the shelter/hut-* acceptance cases'
// siting assertions (#745): each is the initial shelter planner's first
// starter search, recorded from that case's run 0 on the tribal8 baseline
// (under the corridor fixture's terrain where the case raised one) at the
// commit named in the file's Recorded line. The planner previews the
// layouts in order and admits the first whose whole ring is placeable, so
// the first layout is the sited shell.
func TestShelterSitingSnapshots(t *testing.T) {
	t.Parallel()
	for _, c := range []struct {
		name, file string
		// shape is the template the sited shell must be, "irregular" for a
		// grown one, or empty for any hut template.
		shape string
		// maxHeight bounds the shell's height to the strip between two
		// rock rows.
		maxHeight int32
		// carves is a shell that walls itself with the rock rows (#700),
		// digging whatever rock falls inside, rather than fitting between
		// them.
		carves bool
	}{
		{name: "open ground sites a hut", file: "shelter-hut.json.gz"},
		// Since #700 corridors and ten-cell strips no longer grow an irregular
		// shell or take the medium oval: the circle stands against the rows and reuses them as wall.
		{name: "five-cell corridors carve the circle into the rock", file: "shelter-hut-corridor.json.gz", shape: "hut-template-0", carves: true},
		{name: "ten-cell strips carve the circle into the rock", file: "shelter-hut-oval.json.gz", shape: "hut-template-0", carves: true},
		{name: "eight-cell strips take the low oval", file: "shelter-hut-low-oval.json.gz", shape: "hut-template-7", maxHeight: 8},
		{name: "an L-shaped clearing takes the concave template", file: "shelter-hut-concave.json.gz", shape: "concave-l-ne"},
		{name: "two clearings a cell apart take the connector", file: "shelter-hut-connector.json.gz", shape: "connector-ew"},
	} {
		t.Run(c.name, func(t *testing.T) {
			t.Parallel()
			layout := sitedShell(t, "testdata/"+c.file)
			shape := templateName(layout.Shell)
			if c.shape == "" && !isHutTemplate(shape) || c.shape != "" && shape != c.shape {
				t.Fatalf("sited %s at %v, want %q", shape, layout.Room, c.shape)
			}
			if c.maxHeight > 0 && layout.Shell.Bounds().Height > c.maxHeight {
				t.Fatalf("shell bounds %v are not confined to a strip of %d", layout.Shell.Bounds(), c.maxHeight)
			}
			if carved := len(layout.Reused) > 0; carved != c.carves {
				t.Fatalf("reused %d rock cells and mined %d, want carved=%v", len(layout.Reused), len(layout.Mined), c.carves)
			}
		})
	}
}

// sitedShell replays the recorded step's first starter search.
func sitedShell(t *testing.T, path string) policy.StarterLayout {
	t.Helper()
	p, err := snapshot.LoadPlanner(path)
	if err != nil {
		t.Fatal(err)
	}
	if len(p.Shelter) == 0 {
		t.Fatalf("%s records no starter search", path)
	}
	layouts, err := policy.StarterLayouts(p.Shelter[0])
	if err != nil || len(layouts) == 0 {
		t.Fatalf("no layout: %v", err)
	}
	return layouts[0]
}

func isHutTemplate(shape string) bool {
	return len(shape) > len("hut-template-") && shape[:len("hut-template-")] == "hut-template-"
}

// templateName is the hut-style template the footprint is, or "irregular".
func templateName(f domain.RoomFootprint) string {
	b := f.Bounds()
	for x := b.X; x < b.X+b.Width; x++ {
		for z := b.Z; z < b.Z+b.Height; z++ {
			for _, t := range policy.ShellTemplates(policy.ShelterHut) {
				if shell, err := t.Shape(domain.Cell{X: x, Z: z}); err == nil && domain.SameRoomFootprint(shell, f) {
					return t.Name
				}
			}
		}
	}
	return "irregular"
}
