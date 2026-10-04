package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestMedicalReserveRetainsHistoryAcrossManualUnknownAndRestart(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := roundsRequest()
	r.Facts.Colonists = domain.Known(int64(3))
	set := func(n int64) {
		r.Facts.MedicalReserve = policy.MedicalReserveObservation{Catalog: policy.CoreItemFacts(), Items: domain.Known([]policy.MedicineStack{{ID: "medicine", Definition: "MedicineHerbal", Count: n, Perishable: domain.Known(false)}}), Resources: domain.Known([]policy.Amount{{Resource: "MedicineHerbal", Count: n}})}
	}
	set(2)
	out := reviewRounds(t, s, &r)
	if g := roundsGoal(t, out, policy.MaintainMedicalReserves); g.Standard.Finding != domain.FindingUnmet || !out.Review.Latches.MedicalReserve {
		t.Fatal(g)
	}
	set(5)
	out = reviewRounds(t, s, &r)
	if g := roundsGoal(t, out, policy.MaintainMedicalReserves); g.Standard.Finding != domain.FindingUnmet {
		t.Fatal(g)
	}
	r.Facts.MedicalReserve.Items = domain.Unknown[[]policy.MedicineStack]()
	out = reviewRounds(t, s, &r)
	if g := roundsGoal(t, out, policy.MaintainMedicalReserves); g.Standard.Finding != domain.FindingUnclear || g.Standard.Priority != 3 {
		t.Fatal(g)
	}
	r.Enabled = false
	out = reviewRounds(t, s, &r)
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	defer s.Close()
	retained, err := s.LoadRounds(ctx)
	if err != nil || !retained.Latches.MedicalReserve || retained.Enabled {
		t.Fatal(retained, err)
	}
	r.Enabled = true
	set(5)
	out = reviewRounds(t, s, &r)
	if g := roundsGoal(t, out, policy.MaintainMedicalReserves); g.Standard.Finding != domain.FindingUnmet {
		t.Fatal(g)
	}
	set(9)
	r.Facts.UpkeepIssued = map[policy.ConcernID]bool{policy.MaintainMedicalReserves: true}
	out = reviewRounds(t, s, &r)
	recovered := roundsGoal(t, out, policy.MaintainMedicalReserves)
	if recovered.Standard.Finding != domain.FindingMet || out.Review.Latches.MedicalReserve {
		t.Fatal(recovered)
	}
	set(2)
	out = reviewRounds(t, s, &r)
	if g := roundsGoal(t, out, policy.MaintainMedicalReserves); g.Standard.Finding != domain.FindingUnmet || g.Standard.Episode <= recovered.Standard.Episode {
		t.Fatal(g)
	}
	set(5)
	r.Current.Load = "new-load"
	out = reviewRounds(t, s, &r)
	if g := roundsGoal(t, out, policy.MaintainMedicalReserves); g.Standard.Finding != domain.FindingMet {
		t.Fatal(g)
	}
}
