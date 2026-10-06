package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
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
		{"no storage", func(r *ClearanceTarget) { r.Salvage.Candidate.Yields[0].Headroom = domain.Known(int64(0)) }, steelDemand(), false, "missing_storage"},
		{"satisfied", func(*ClearanceTarget) {}, domain.Known([]ResourceDemand{}), false, ""},
	} {
		t.Run(tt.name, func(t *testing.T) {
			row := ClearanceTarget{EntityID: "ruin", DefName: "Wall", Minimum: domain.Cell{X: 95, Z: 95}, Maximum: domain.Cell{X: 95, Z: 95}, Deconstructible: true,
				Salvage: &SalvageEvidence{Safe: domain.Known(true), Candidate: SourceCandidate(CandidateSalvage, "", domain.Known(100.0), domain.Known(120.0), true, 75, SourceYield(ResourceKey{Def: "Steel"}, 3, 0, domain.Known(int64(75))))}}
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

// A shortage alone raises salvage demand: the default steel floor (#875)
// with no steel in stock is demand LootDemand hands the salvage ranking,
// so ship chunks and steel-yielding ruins score without an operator target.
func TestLootDemandFromDefaultSteelShortage(t *testing.T) {
	p := RoundsPolicy{ResourceTargets: DefaultResourceTargets()}
	f := RoundsFacts{Resources: domain.Known([]Amount{{Resource: "Steel", Count: 0}})}
	demand, err := LootDemand(p, f)
	if err != nil {
		t.Fatal(err)
	}
	rows, known := demand.Value()
	steel := int64(0)
	for _, row := range rows {
		if row.Key.Def == "Steel" {
			steel = row.Count
		}
	}
	if !known || steel != DefaultResourceTargets()["Steel"] {
		t.Fatal(rows, known)
	}
}
