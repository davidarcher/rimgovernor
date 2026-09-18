package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"os"
	"reflect"
	"testing"
)

func TestColonyStartingSuppliesPreservesRowsAndUnavailableCensus(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	r := &o.ColonyFactsReply{}
	if err = protojson.Unmarshal(data, r); err != nil {
		t.Fatal(err)
	}
	id, err := contextIdentity(r.GetObserved().Context)
	if err != nil {
		t.Fatal(err)
	}
	mapID := r.GetObserved().Context.Identity.MapId
	r.GetObserved().ForbiddenSupplies = []*o.EntityRef{{Id: proto.String("Thing_Pemmican1"), DefName: proto.String("Pemmican"), MapId: mapID, Position: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}}}
	p, err := DecodeColony(r, id)
	if err != nil {
		t.Fatal(err)
	}
	rows, known := p.Facts.StartingSupplies.Value()
	if !known || !reflect.DeepEqual(rows, []policy.StartingSupply{{Thing: "Thing_Pemmican1", Definition: "Pemmican", Cell: domain.Cell{X: 1, Z: 2}}}) {
		t.Fatal(rows, known)
	}
	r.GetObserved().ForbiddenSupplies[0].Position.X = proto.Int32(3)
	if rows[0].Cell.X != 1 {
		t.Fatal("projection aliases wire cells")
	}
	r.GetObserved().ForbiddenSupplies = nil
	p, err = DecodeColony(r, id)
	if err != nil {
		t.Fatal(err)
	}
	rows, known = p.Facts.StartingSupplies.Value()
	if !known || len(rows) != 0 {
		t.Fatal("complete empty census lost", rows, known)
	}
	r.GetObserved().Issues = append(r.GetObserved().Issues, &o.ReadIssue{Field: proto.String("forbidden_supplies"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_UNSUPPORTED.Enum(), Detail: proto.String("unavailable")}})
	p, err = DecodeColony(r, id)
	if err != nil {
		t.Fatal(err)
	}
	if _, known = p.Facts.StartingSupplies.Value(); known {
		t.Fatal("unavailable census initialized supplies")
	}
}
