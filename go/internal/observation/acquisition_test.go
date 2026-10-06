package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// A census row carries its designation age and taken flag (#1043).
func TestColonyAcquisitionMapsDesignationAgeAndTaken(t *testing.T) {
	v := &o.ColonyFactsSnapshot{HuntCensus: huntCensusFacts(true).HuntCensus, Acquisition: []*o.AcquisitionFacts{
		{Source: bridge.NewRef("deer"), Resource: proto.String("Corpse_Deer"), Hunt: proto.Bool(true), Food: proto.Bool(true), Fogged: proto.Bool(false), InMentalState: proto.Bool(false), Designated: proto.Bool(true), DesignatedTick: proto.Int64(1200), Taken: proto.Bool(true)},
		{Source: bridge.NewRef("oak"), Designated: proto.Bool(false), Taken: proto.Bool(false)},
	}}
	rows, ok := ColonyAcquisition(v, heads(&o.EntityRef{Id: proto.String("deer"), DefName: proto.String("Deer")}, &o.EntityRef{Id: proto.String("oak")})).Value()
	if !ok || len(rows) != 2 {
		t.Fatal(rows, ok)
	}
	if rows[0].DesignatedTick != domain.Tick(1200) || !rows[0].Taken || !rows[0].Designated {
		t.Fatal(rows[0])
	}
	if rows[1].DesignatedTick != 0 || rows[1].Taken {
		t.Fatal(rows[1])
	}
}
