package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestRemoteSalvageSafetyAndDemand(t *testing.T) {
	reach := tribal8Reach()
	reach.Armed, reach.FreeHaulers = domain.Known(int64(6)), domain.Known(int64(3))
	for _, tt := range []struct {
		name   string
		edit   func(*ClearanceTarget)
		demand domain.Fact[[]ResourceDemand]
		want   bool
		reason string
	}{
		{"steel shortage", func(*ClearanceTarget) {}, steelDemand(), true, ""},
		{"roof support", func(r *ClearanceTarget) { r.RoofBlocker = "collapse" }, steelDemand(), false, "roof_support_risk"},
		{"sealed shrine", func(r *ClearanceTarget) { r.AncientDanger = true }, steelDemand(), false, "ancient_danger"},
		{"casket", func(r *ClearanceTarget) { r.Class = "ancient_casket" }, steelDemand(), false, "casket"},
		{"hazard", func(r *ClearanceTarget) { r.Salvage.Safe = domain.Known(false) }, steelDemand(), false, "route_unsafe"},
		{"satisfied", func(*ClearanceTarget) {}, domain.Known([]ResourceDemand{}), false, ""},
	} {
		t.Run(tt.name, func(t *testing.T) {
			row := ClearanceTarget{EntityID: "ruin", DefName: "Wall", Minimum: domain.Cell{X: 95, Z: 95}, Maximum: domain.Cell{X: 95, Z: 95}, Deconstructible: true,
				Salvage: &SalvageEvidence{Safe: domain.Known(true), Candidate: AcquisitionCandidate{PathDistance: domain.Known(120.0), Labor: domain.Known(100.0), NeedsHaul: true, UnitsPerTrip: 75, Yields: []AcquisitionYield{{ResourceQuantity: ResourceQuantity{Key: ResourceKey{Def: "Steel"}, Count: 3}, Headroom: domain.Known(int64(75))}}}}}
			tt.edit(&row)
			got, holds, err := FilterRemoteSalvage([]ClearanceTarget{row}, RemoteWorkRequest{Reach: reach, Demand: tt.demand})
			if err != nil || len(got) != 1 || got[0].SalvageSelected != tt.want {
				t.Fatalf("got %v holds %v err %v", got, holds, err)
			}
			if tt.reason != "" && (len(holds) != 1 || holds[0].Reason != tt.reason) {
				t.Fatal(holds)
			}
			if got[0].InHome {
				t.Fatal("salvage changed Home")
			}
		})
	}
}
