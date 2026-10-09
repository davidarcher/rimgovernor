package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The paddock is closed only with every core ring section standing and, when
// the plan fences the killbox lane, the fence too; a ring gap never closes it.
func TestPaddockClosed(t *testing.T) {
	fenced := policy.LayoutPlan{Reservations: []policy.LayoutReservation{{Kind: policy.ReserveKillboxFence}}}
	unfenced := policy.LayoutPlan{}
	gap := policy.LayoutPlan{Reservations: []policy.LayoutReservation{{Kind: policy.ReservePerimeterGap}}}
	ring := func(fence, built bool) store.DefenseLayoutRecord {
		r := store.DefenseLayoutRecord{Tiers: []store.DefenseTierRecord{
			{Name: "perimeter-00", Built: true}, {Name: "perimeter-01", Built: built},
			{Name: "perimeter-outer-00"}, {Name: policy.TierTurrets},
		}}
		if fence {
			r.Tiers = append(r.Tiers, store.DefenseTierRecord{Name: "perimeter-fence-00", Built: built})
		}
		return r
	}
	cases := []struct {
		name   string
		record store.DefenseLayoutRecord
		plan   policy.LayoutPlan
		want   bool
	}{
		{"ring and fence standing", ring(true, true), fenced, true},
		{"ring section open", ring(true, false), fenced, false},
		{"fence not recorded", ring(false, true), fenced, false},
		{"no killbox fence planned", ring(false, true), unfenced, true},
		{"ring gap", ring(true, true), gap, false},
		{"no ring recorded", store.DefenseLayoutRecord{}, unfenced, false},
	}
	for _, c := range cases {
		if got := paddockClosed(c.record, c.plan); got != c.want {
			t.Errorf("%s: closed = %v", c.name, got)
		}
	}
	// The fence standing and the ring open is not closed either.
	open := store.DefenseLayoutRecord{Tiers: []store.DefenseTierRecord{{Name: "perimeter-00"}, {Name: "perimeter-fence-00", Built: true}}}
	if paddockClosed(open, fenced) {
		t.Error("an open ring with a fence reads closed")
	}
}
