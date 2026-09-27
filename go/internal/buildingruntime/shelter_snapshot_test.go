package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

// The shelter siting snapshots replace the retired staged-shell acceptance cases'
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
		// shape is the concave template the sited shell must be.
		shape string
	}{
		{name: "an L-shaped clearing takes the concave template", file: "shelter-concave.json.gz", shape: "concave-l-ne"},
		{name: "two clearings a cell apart take the connector", file: "shelter-connector.json.gz", shape: "connector-ew"},
	} {
		t.Run(c.name, func(t *testing.T) {
			t.Parallel()
			layout := sitedShell(t, "testdata/"+c.file)
			shape := templateName(layout.Shell)
			if shape != c.shape {
				t.Fatalf("sited %s at %v, want %q", shape, layout.Room, c.shape)
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

// templateName is the concave template the footprint is, or "irregular".
func templateName(f domain.RoomFootprint) string {
	b := f.Bounds()
	for x := b.X; x < b.X+b.Width; x++ {
		for z := b.Z; z < b.Z+b.Height; z++ {
			for _, t := range policy.ShellTemplates() {
				if shell, err := t.Shape(domain.Cell{X: x, Z: z}); err == nil && domain.SameRoomFootprint(shell, f) {
					return t.Name
				}
			}
		}
	}
	return "irregular"
}
