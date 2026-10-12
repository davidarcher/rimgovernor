package policy

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestRingMarkersScaleWithCapableAdults(t *testing.T) {
	for capable, want := range map[int]int{0: 4, 4: 4, 7: 7, 12: 12, 40: 12} {
		if got := RingMarkersFor(capable); got != want {
			t.Errorf("%d capable adults: %d markers, want %d", capable, got, want)
		}
	}
}

func TestRingTemplatePlacesOneMarkerPerSlotOnTheInnerEdge(t *testing.T) {
	room := ringFromBlock(Rectangle{X: 20, Z: 30, Width: 11, Height: 11}, RingMaxMarkers)
	pieces := RingTemplate(room)
	if len(pieces) != RingMaxMarkers {
		t.Fatalf("%d pieces", len(pieces))
	}
	seen := map[domain.Cell]bool{}
	slots := map[string]bool{}
	in := room.Region.Bounds()
	for _, p := range pieces {
		c := p.Minimum
		onEdge := c.X == in.X || c.X == in.X+in.Width-1 || c.Z == in.Z || c.Z == in.Z+in.Height-1
		corner := (c.X == in.X || c.X == in.X+in.Width-1) && (c.Z == in.Z || c.Z == in.Z+in.Height-1)
		if p.DefName != RingMarkerDef || p.Minimum != p.Maximum || seen[c] || slots[p.Slot] || !contains(in, c) || !onEdge || corner {
			t.Fatalf("bad piece %+v", p)
		}
		seen[c], slots[p.Slot] = true, true
	}
	if len(RingTemplate(PlannedRoom{Role: PlannedSparringRing, Region: RectRegion(in), Markers: 5})) != 5 {
		t.Fatal("template ignores Markers")
	}
}

func TestSparringMarkerDefIsDefinedByTheNativeMod(t *testing.T) {
	things, err := os.ReadFile(filepath.Join("..", "..", "..", "integrations", "rimgovernor-native", "Defs", "ThingDefs", "SparringMarker.xml"))
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(things), "<defName>"+RingMarkerDef+"</defName>") {
		t.Fatal("ThingDef missing from the native mod", RingMarkerDef)
	}
}
