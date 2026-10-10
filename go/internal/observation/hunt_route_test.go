package observation

import (
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// Evaluated-unsafe, skipped and unevaluated routes reach policy as three distinct hold reasons.
func TestHuntRouteEvidenceReachesPolicyDistinct(t *testing.T) {
	tables := huntTables(t, &o.EntityRef{Id: proto.String("deer"), DefName: proto.String("Deer"), Position: &c.Cell{X: proto.Int32(10), Z: proto.Int32(10)}})
	cases := []struct {
		route *o.HuntRoute
		want  string
	}{
		{&o.HuntRoute{PreyId: proto.String("deer"), Safe: proto.Bool(false)}, "ann no_safe_route"},
		{&o.HuntRoute{PreyId: proto.String("deer"), Skipped: proto.Bool(true)}, "ann route_skipped"},
		{&o.HuntRoute{PreyId: proto.String("other"), Safe: proto.Bool(true)}, "ann route_unevaluated"},
	}
	for _, tc := range cases {
		v := huntCensusFacts(true)
		v.HuntCensus.Hunters[0].Routes = []*o.HuntRoute{tc.route}
		_, holds := decodeAcquisition(v, tables)
		if len(holds) != 1 || holds[0].Reason != "no_hunter" || len(holds[0].Detail) != 1 || holds[0].Detail[0] != tc.want {
			t.Fatal(tc.want, holds)
		}
	}
}
