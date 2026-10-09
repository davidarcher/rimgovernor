package observation

import (
	"errors"
	"os"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// TestColonyFrameWithoutBiomeFailsWhereDarknessDecides: a frame that
// carries a sowing climate but no biome, or a biome but no catalog, is an
// error naming the missing piece rather than a lit biome.
func TestColonyFrameWithoutBiomeFailsWhereDarknessDecides(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	r := &o.ColonyFactsReply{}
	if err = protojson.Unmarshal(data, r); err != nil {
		t.Fatal(err)
	}
	expected := Identity{Colony: "colony", Load: "load", Map: 0, Tick: 7, NativeGeneration: domain.Known(domain.NativeGeneration(1))}
	r.GetObserved().FoodClimate = &o.FoodClimate{GrowingDays: proto.Float64(40), GrowingDaysRemaining: proto.Float64(10), GrowingDaysUntil: proto.Float64(0), NonGrowingDays: proto.Float64(22), SowingNow: proto.Bool(true), DayOfYear: proto.Int32(35), Season: proto.String("Fall")}
	catalog := decodeCatalog(t, &o.DefinitionCatalog{ThingDefs: []*d.ThingDef{{DefName: "Anchor"}}, StatValues: &o.DefStatTable{}})
	r.GetObserved().Biome = nil
	if _, err = DecodeColony(r, expected, bridge.Tables{Catalog: catalog}); !errors.Is(err, policy.ErrOutdoorsDarkUnknown) || !strings.Contains(err.Error(), "no biome") {
		t.Fatal("a sowing climate without a biome did not fail loudly:", err)
	}
	r.GetObserved().Biome = proto.String("FixtureLitBiome")
	if _, err = DecodeColony(r, expected, bridge.Tables{}); !errors.Is(err, policy.ErrOutdoorsDarkUnknown) || !strings.Contains(err.Error(), "no definition catalog") {
		t.Fatal("a sowing climate without a catalog did not fail loudly:", err)
	}
}
