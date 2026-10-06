package snapshot

import (
	"path/filepath"
	"reflect"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Every recorded food plan and acquisition census plans and ranks the same
// through the supply-candidate adapters as directly.
func TestRecordedSnapshotsPlanAndRankIdenticallyThroughSupplyCandidates(t *testing.T) {
	files, err := filepath.Glob("testdata/*.json*")
	if err != nil {
		t.Fatal(err)
	}
	plans, censuses := 0, 0
	for _, file := range files {
		if strings.HasPrefix(filepath.Base(file), "planner-") {
			continue
		}
		r, err := Load(file)
		if err != nil {
			continue
		}
		if plan, known := r.Facts.FoodPlan.Value(); known {
			plans++
			checkFoodPlan(t, file, plan)
		}
		if r.Projection == nil {
			continue
		}
		if rows, known := r.Projection.Acquisition.Value(); known && len(rows) > 0 {
			censuses++
			checkAcquisitionRank(t, file, rows)
		}
	}
	if plans == 0 || censuses == 0 {
		t.Fatalf("recordings carry %d food plans and %d acquisition censuses", plans, censuses)
	}
}

func checkFoodPlan(t *testing.T, file string, plan policy.FoodPlan) {
	t.Helper()
	var direct, adapted []policy.FoodChannel
	for _, rows := range [][]policy.FoodPlanEntry{plan.Portfolio, plan.Unknown} {
		for _, e := range rows {
			direct = append(direct, e.Channel)
			c, ok := policy.FoodChannelOfSupply(policy.SupplyCandidateOfFood(e.Channel))
			if !ok {
				t.Fatalf("%s: %s/%s has no food channel through the adapter", file, e.Channel.Kind, e.Channel.ID)
			}
			adapted = append(adapted, c)
		}
	}
	for _, labor := range []float64{0, 20000, 200000} {
		for _, emergency := range []float64{0, 100} {
			request := func(channels []policy.FoodChannel) policy.FoodPlanRequest {
				return policy.FoodPlanRequest{Demand: plan.Forecast, ReserveDays: 1, MinDays: 3, TargetDays: 10, EmergencyDays: emergency, Channels: domain.Known(channels), Labor: domain.Known(labor)}
			}
			want, werr := policy.SupplyFoodPlan(request(direct))
			got, gerr := policy.SupplyFoodPlan(request(adapted))
			if (werr == nil) != (gerr == nil) || !reflect.DeepEqual(want, got) {
				t.Fatalf("%s labor=%v emergency=%v: plans differ\n%s\n%s (%v, %v)", file, labor, emergency, want.Explain(), got.Explain(), werr, gerr)
			}
		}
	}
}

func checkAcquisitionRank(t *testing.T, file string, rows []policy.AcquisitionSource) {
	t.Helper()
	seen := map[string]bool{}
	for _, row := range rows {
		resource := policy.Resource(row.Resource)
		if seen[row.Resource] {
			continue
		}
		seen[row.Resource] = true
		headroom := domain.Known(int64(500))
		direct := policy.AcquisitionSourceCandidates(resource, rows, domain.Cell{}, headroom)
		adapted := make([]policy.AcquisitionCandidate, len(direct))
		for i, c := range direct {
			var ok bool
			if adapted[i], ok = policy.AcquisitionCandidateOfSupply(policy.SupplyCandidateOfAcquisition(c)); !ok {
				t.Fatalf("%s: %s lost through the adapter", file, c.ID)
			}
		}
		demand := policy.ResourceDeficitDemand(resource, 300)
		want, werr := policy.RankResourceCandidates(demand, direct, policy.AcquisitionCompetition{})
		got, gerr := policy.RankResourceCandidates(demand, adapted, policy.AcquisitionCompetition{})
		if (werr == nil) != (gerr == nil) || !reflect.DeepEqual(want, got) {
			t.Fatalf("%s %s: rankings differ\n%v\n%v (%v, %v)", file, resource, want, got, werr, gerr)
		}
	}
}
