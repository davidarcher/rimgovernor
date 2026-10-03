package observation

import (
	"testing"

	"google.golang.org/protobuf/proto"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func siteRow(id, def string, status o.BuildingStatus, x, z int32) *o.BuildingState {
	return &o.BuildingState{Building: &o.EntityRef{Id: proto.String(id), DefName: proto.String(def), Position: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}}, Status: status.Enum()}
}

// The sites are the finished buildings of the defs a held ritual's pattern
// requires: a blueprint, a frame or another def is not a site.
func TestRitualSitesAreFinishedRequiredBuildings(t *testing.T) {
	built, frame := o.BuildingStatus_BUILDING_STATUS_BUILT, o.BuildingStatus_BUILDING_STATUS_FRAME
	census := &bridge.BuildingCensus{Rows: bridge.NewBuildings(
		siteRow("Thing_1", "Lectern", built, 4, 5),
		siteRow("Thing_2", "Lectern", frame, 6, 7),
		siteRow("Thing_3", "Bed", built, 8, 9),
	)}
	ideology := domain.Known(policy.Ideoligion{
		Defs:  policy.IdeologyDefs{Rituals: map[string]policy.RitualDef{"Sermon": {Name: "Sermon", RequiredBuildings: []string{"Lectern"}}}},
		Facts: policy.IdeoligionFacts{Rituals: []policy.HeldRitual{{ID: "Precept_1", Pattern: "Sermon"}}},
	})
	got, ok := ritualSites(census, ideology).Value()
	want := policy.RitualSite{ID: "Thing_1", Def: "Lectern", Cell: domain.Cell{X: 4, Z: 5}}
	if !ok || len(got) != 1 || got[0] != want {
		t.Fatalf("%+v %v", got, ok)
	}
	if _, ok := ritualSites(nil, ideology).Value(); ok {
		t.Fatal("no building table is unknown")
	}
	if _, ok := ritualSites(census, domain.Unknown[policy.Ideoligion]()).Value(); ok {
		t.Fatal("no ideoligion is unknown")
	}
}
