package observation

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// An absent target_a is an older producer (unknown); an unavailable one is
// a job with no target (#643).
func TestJobTarget(t *testing.T) {
	i := func(v int32) *int32 { return &v }
	id := "Thing_Steel1"
	cell := domain.Known(domain.Cell{X: 4, Z: 5})
	for _, c := range []struct {
		in   *o.TargetRef
		want domain.Fact[policy.JobTarget]
	}{
		{nil, domain.Unknown[policy.JobTarget]()},
		{&o.TargetRef{Target: &o.TargetRef_Entity{Entity: &o.EntityRef{Id: &id, Position: &commonpb.Cell{X: i(4), Z: i(5)}}}}, domain.Known(policy.JobTarget{Thing: id, Cell: cell})},
		{&o.TargetRef{Target: &o.TargetRef_Cell{Cell: &commonpb.Cell{X: i(4), Z: i(5)}}}, domain.Known(policy.JobTarget{Cell: cell})},
		{&o.TargetRef{Target: &o.TargetRef_Unavailable{Unavailable: &commonpb.Unavailable{}}}, domain.Known(policy.JobTarget{})},
	} {
		if got := jobTarget(c.in); !reflect.DeepEqual(got, c.want) {
			t.Fatal(c.in, got, c.want)
		}
	}
}
