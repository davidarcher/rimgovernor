package bridge

import (
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// An outfit may allow apparel the pawn cannot wear (all-DLC CI, 2026-10-02);
// a duplicate is still refused.
func TestApparelPolicyAllowsUnwearableFilterDefs(t *testing.T) {
	v := &o.ApparelPolicyState{Token: proto.String("p1"), Child: proto.Bool(false), Slave: proto.Bool(false), IncapableOfViolence: proto.Bool(false), Drafted: proto.Bool(false),
		Name: proto.String("Anything"), MinHitPoints: proto.Float32(0), MaxHitPoints: proto.Float32(1), MinQuality: proto.Int32(0), MaxQuality: proto.Int32(6), ExcludesTainted: proto.Bool(false),
		AllowedDefs: []string{"Apparel_Parka", "Apparel_BabyOnesie"}}
	if err := validateApparelPolicy(v); err != nil {
		t.Fatal(err)
	}
	v.AllowedDefs = append(v.AllowedDefs, "Apparel_Parka")
	if validateApparelPolicy(v) == nil {
		t.Fatal("duplicate allowed def passed")
	}
}
