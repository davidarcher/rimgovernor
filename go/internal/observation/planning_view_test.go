package observation

import (
	"bytes"
	"compress/gzip"
	"fmt"
	"io"
	"maps"
	"os"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func readGzip(t *testing.T, path string) []byte {
	t.Helper()
	file, err := os.Open(path)
	if err != nil {
		t.Fatal(err)
	}
	defer file.Close()
	zr, err := gzip.NewReader(file)
	if err != nil {
		t.Fatal(err)
	}
	data, err := io.ReadAll(zr)
	if err != nil {
		t.Fatal(err)
	}
	return data
}

// recordedCatalog is a definition catalog recorded from a running game: the
// def rows and stat table the planning views read: testdata/full_catalog.pb.gz,
// the whole game with every expansion.
func recordedCatalog(t *testing.T) *bridge.DefinitionCatalog {
	t.Helper()
	data := readGzip(t, "testdata/full_catalog.pb.gz")
	wire := &o.DefinitionCatalog{}
	if err := proto.Unmarshal(data, wire); err != nil {
		t.Fatal(err)
	}
	catalog, err := bridge.DecodeDefinitionCatalog(wire, wire.GetContext().GetIdentity())
	if err != nil {
		t.Fatal(err)
	}
	return catalog
}

// TestPlanningViewsMatchTheRecordedGolden pins every planning view over a
// catalog recorded from the game. The golden was written when the views were
// compared field by field with the rows of the retired native planning
// writer on this same catalog and matched (#1731); RG_UPDATE_GOLDEN=1
// rewrites it after a deliberate change to a view.
func TestPlanningViewsMatchTheRecordedGolden(t *testing.T) {
	catalog := recordedCatalog(t)
	names := slices.Sorted(maps.Keys(catalog.ThingDefs))
	names = append(names, slices.Sorted(maps.Keys(catalog.TerrainDefs))...)
	slices.Sort(names)
	var out bytes.Buffer
	rows, stuffed := 0, 0
	for _, name := range names {
		view, listed, err := planningView(catalog, name)
		if err != nil {
			t.Fatalf("%s: %v", name, err)
		}
		if !listed {
			continue
		}
		rows++
		if view.Stuffed {
			stuffed++
		}
		fmt.Fprintf(&out, "%+v\n", view)
	}
	if rows < 100 || stuffed == 0 {
		t.Fatalf("recorded catalog lists %d planning rows (%d stuffed)", rows, stuffed)
	}
	if os.Getenv("RG_UPDATE_GOLDEN") != "" {
		var zipped bytes.Buffer
		zw, _ := gzip.NewWriterLevel(&zipped, gzip.BestCompression)
		zw.Write(out.Bytes())
		zw.Close()
		if err := os.WriteFile("testdata/planning_views.golden.gz", zipped.Bytes(), 0o644); err != nil {
			t.Fatal(err)
		}
		return
	}
	want := bytes.Split(readGzip(t, "testdata/planning_views.golden.gz"), []byte("\n"))
	got := bytes.Split(out.Bytes(), []byte("\n"))
	if len(want) != len(got) {
		t.Fatalf("%d planning rows, golden holds %d", len(got), len(want))
	}
	for i := range want {
		if !bytes.Equal(want[i], got[i]) {
			t.Fatalf("planning row %d differs from the golden:\n got %s\nwant %s", i, got[i], want[i])
		}
	}
}
