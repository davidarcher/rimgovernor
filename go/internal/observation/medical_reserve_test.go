package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestMedicalReserveProjectionPreservesIndependentUnknowns(t *testing.T) {
	u := &o.UpkeepFacts{Items: []*o.UpkeepItem{{Item: bridge.NewRef("med"), Count: proto.Int64(5), Forbidden: proto.Bool(false)}}}
	tables := heads(&o.EntityRef{Id: proto.String("med"), DefName: proto.String("MedicineHerbal")})
	tables.Catalog = itemCatalog(t, map[string]itemFact{"MedicineHerbal": {medicine: true}}, nil)
	v := &o.ColonyFactsSnapshot{ColonistCount: proto.Uint32(3), Resources: []*o.Quantity{{DefName: proto.String("MedicineHerbal"), Units: proto.Int64(5)}}, Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	f := ColonyMedicalReserve(v, tables)
	f.Catalog = policy.CoreItemFacts()
	r, err := policy.ReviewMedicalReserve(f, true, policy.DefaultMedicalReservePolicy())
	if err != nil || !r.Active {
		t.Fatal(r, err)
	}
	if n, k := r.Replenish.Value(); !k || n != 4 {
		t.Fatal(r)
	}
	unclassified := tables
	unclassified.Catalog = itemCatalog(t, map[string]itemFact{"Steel": {}}, nil)
	f = ColonyMedicalReserve(v, unclassified)
	if _, known := f.Items.Value(); known {
		t.Fatal("a def the catalog has no facts for became an empty census")
	}
	v.Resources[0].Units = nil
	f = ColonyMedicalReserve(v, tables)
	if _, known := f.Resources.Value(); known {
		t.Fatal("missing stock counted as zero")
	}
}
