package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// TestFullCatalogRecordingDecodes pins testdata/full_catalog.pb.gz: the whole
// definition catalog of the game with every expansion, untrimmed (every
// ThingDef, TerrainDef, RecipeDef and other def row, the research projects,
// stat table and DLC sections). RG_CATALOG=<uncompressed copy> points
// recordedCatalog at it for the planning tests.
func TestFullCatalogRecordingDecodes(t *testing.T) {
	wire := &o.DefinitionCatalog{}
	if err := proto.Unmarshal(readGzip(t, "testdata/full_catalog.pb.gz"), wire); err != nil {
		t.Fatal(err)
	}
	catalog, err := bridge.DecodeDefinitionCatalog(wire, wire.GetContext().GetIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if len(catalog.ThingDefs) < 3000 || len(catalog.TerrainDefs) < 300 || len(wire.GetDefs().GetRecipeDefs()) < 400 || catalog.Odyssey == nil || catalog.Anomaly == nil {
		t.Fatalf("recorded catalog is trimmed: %d things, %d terrains, %d recipes", len(catalog.ThingDefs), len(catalog.TerrainDefs), len(wire.GetDefs().GetRecipeDefs()))
	}
}
