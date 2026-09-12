package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestMedicalReserveProjectionPreservesIndependentUnknowns(t *testing.T) {
	u := &o.UpkeepFacts{Items: []*o.UpkeepItem{{Item: &o.EntityRef{Id: proto.String("med"), DefName: proto.String("MedicineHerbal")}, Count: proto.Int64(5), Medicine: proto.Bool(true), Forbidden: proto.Bool(false), Perishable: proto.Bool(false)}}}
	v := &o.ColonyFactsSnapshot{ColonistCount: proto.Uint32(3), Resources: []*o.Quantity{{DefName: proto.String("MedicineHerbal"), Units: proto.Int64(5)}}, Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	f := colonyMedicalReserve(v)
	r, err := policy.ReviewMedicalReserve(f, true, policy.DefaultMedicalReservePolicy())
	if err != nil || !r.Active {
		t.Fatal(r, err)
	}
	if n, k := r.Replenish.Value(); !k || n != 4 {
		t.Fatal(r)
	}
	u.Items[0].Medicine = nil
	f = colonyMedicalReserve(v)
	if _, known := f.Items.Value(); known {
		t.Fatal("missing medicine classification became empty census")
	}
	u.Items[0].Medicine = proto.Bool(true)
	v.Resources[0].Units = nil
	f = colonyMedicalReserve(v)
	if _, known := f.Resources.Value(); known {
		t.Fatal("missing stock counted as zero")
	}
}
