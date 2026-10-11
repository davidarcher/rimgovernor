package campaign

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

// The probe asks the way the service does: a bare scope leaves herdSize
// unset on wild hunt rows and DecodeColony rejects them.
func TestColonyFactsWireCarriesHerdRadiusAndHuntBudget(t *testing.T) {
	raw, err := colonyFactsWire(map[string]any{"colonyId": "colony", "loadToken": "load", "mapId": 1})
	if err != nil {
		t.Fatal(err)
	}
	q := &o.ColonyFactsRequest{}
	if err := protojson.Unmarshal(raw, q); err != nil {
		t.Fatal(err)
	}
	if q.GetScope().GetExpectedIdentity().GetColonyId() != "colony" {
		t.Fatalf("identity lost: %s", raw)
	}
	if q.HerdRadius == nil || q.GetHerdRadius() != bridge.HerdRadius || q.HuntRouteBudgetMs == nil || q.GetHuntRouteBudgetMs() != bridge.HuntRouteBudgetMS || q.HuntPredatorMarginCells == nil {
		t.Fatalf("request omits the hunt facts parameters: %s", raw)
	}
}
