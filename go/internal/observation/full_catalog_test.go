package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
)

// TestFullCatalogRecordingDecodes pins testdata/full_catalog.pb.gz: the whole
// definition catalog of the game with every expansion, untrimmed (every
// ThingDef, TerrainDef, RecipeDef and other def row, the research projects,
// stat table and DLC sections); recordedCatalog loads it for the planning tests.
func TestFullCatalogRecordingDecodes(t *testing.T) {
	wire := recordedcatalog.Wire(t)
	catalog := recordedcatalog.Catalog(t)
	if len(catalog.ThingDefs) < 3000 || len(catalog.TerrainDefs) < 300 || len(wire.GetDefs().GetRecipeDefs()) < 400 || catalog.Odyssey == nil || !catalog.HasAnomaly() {
		t.Fatalf("recorded catalog is trimmed: %d things, %d terrains, %d recipes", len(catalog.ThingDefs), len(catalog.TerrainDefs), len(wire.GetDefs().GetRecipeDefs()))
	}
}
