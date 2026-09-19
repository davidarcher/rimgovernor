package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
	"testing"
)

func TestHuntSelectionCosts(t *testing.T) {
	herd := AcquisitionSource{ID: "herd", Resource: "Corpse_Boomalope", Token: "t", Hunt: true, Food: true, Yield: 1, NutritionYield: 10, RevengeChance: 0.05, HerdSize: 3}
	deer := herd
	deer.ID = "deer"
	deer.Resource = "Corpse_Deer"
	deer.RevengeChance = 0
	deer.HerdSize = 1
	down := herd
	down.ID = "down"
	down.Downed = true
	for _, tc := range []struct {
		rows []AcquisitionSource
		want string
	}{
		{[]AcquisitionSource{herd, deer}, "deer"},
		{[]AcquisitionSource{herd, deer, down}, "down"},
	} {
		got, err := SelectAcquisition(domain.Known(tc.rows), domain.Known(1.0), domain.Known(0.0), true, nil, domain.Known(1))
		if err != nil || len(got) != 1 || got[0].ID != tc.want {
			t.Fatalf("got %v, %v; want %s", got, err, tc.want)
		}
	}
	for _, bad := range []float64{-1, 1.1, math.NaN(), math.Inf(1)} {
		herd.RevengeChance = bad
		if _, err := SelectAcquisition(domain.Known([]AcquisitionSource{herd}), domain.Known(1.0), domain.Known(0.0), true, nil, domain.Known(1)); err == nil {
			t.Fatal("accepted invalid revenge", bad)
		}
	}
}

func TestHuntChannelsExposeRiskAndPursuitWork(t *testing.T) {
	sources := []AcquisitionSource{
		{ID: "melee", Hunt: true, Food: true, NutritionYield: 10, MeleeOnly: true, HerdSize: 1},
		{ID: "ranged", Hunt: true, Food: true, NutritionYield: 10, WeaponRange: 25, RevengeChance: 0.05, HerdSize: 3},
		{ID: "down", Hunt: true, Food: true, NutritionYield: 10, Downed: true, RevengeChance: 0.5, HerdSize: 3},
	}
	rows := HuntChannels(sources)
	melee, _ := rows[0].WorkPerDay.Value()
	ranged, _ := rows[1].WorkPerDay.Value()
	down, _ := rows[2].WorkPerDay.Value()
	if !(down < ranged && ranged < melee) {
		t.Fatal("pursuit work", rows)
	}
	if math.Abs(rows[1].Risk[0].Weight-0.15) > 1e-9 || rows[2].Risk[0].Weight != 0 {
		t.Fatal("revenge risk", rows)
	}
	found := false
	for _, term := range rows[1].Terms {
		if term.Name == "herd_size" && term.Value == 3 {
			found = true
		}
	}
	if !found {
		t.Fatal("herd cost unexplained", rows)
	}
}
