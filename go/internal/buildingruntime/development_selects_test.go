package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func TestDevelopmentSelectsCommittedBenchGoals(t *testing.T) {
	for _, tc := range []struct {
		name string
		goal domain.ConcernID
		row  store.RoundsDevelopmentRow
		want bool
	}{
		{"committed resource", policy.MaintainResource, store.RoundsDevelopmentRow{Goal: policy.MaintainResource, Committed: true}, true},
		{"committed equipment", policy.MaintainEquipment, store.RoundsDevelopmentRow{Goal: policy.MaintainEquipment, Committed: true}, true},
		{"committed sleeping", policy.MaintainHousing, store.RoundsDevelopmentRow{Goal: policy.MaintainHousing, Committed: true}, false},
		{"selected sleeping", policy.MaintainHousing, store.RoundsDevelopmentRow{Goal: policy.MaintainHousing, Selected: true}, true},
		{"other goal's row", policy.MaintainResource, store.RoundsDevelopmentRow{Goal: policy.MaintainEquipment, Committed: true}, false},
	} {
		if got := developmentSelects([]store.RoundsDevelopmentRow{tc.row}, tc.goal); got != tc.want {
			t.Errorf("%s: got %v, want %v", tc.name, got, tc.want)
		}
	}
}
