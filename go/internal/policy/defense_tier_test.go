package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestDefensePromoted(t *testing.T) {
	k, u := domain.Known[float64], domain.Unknown[float64]()
	for _, tc := range []struct {
		name       string
		raid, have domain.Fact[float64]
		want       bool
	}{
		{"outmatched", k(500), k(200), true},
		{"at floor outmatched", k(300), k(100), true},
		{"below floor", k(299), k(0), false},
		{"equal capacity", k(500), k(500), false},
		{"capable", k(500), k(900), false},
		{"unknown raid", u, k(0), false},
		{"unknown capacity", k(900), u, false},
	} {
		if got := DefensePromoted(tc.raid, tc.have); got != tc.want {
			t.Errorf("%s: promoted = %v", tc.name, got)
		}
	}
}

func TestDefenseBuildTier(t *testing.T) {
	for _, tc := range []struct {
		promoted, ring, roamer bool
		want                   domain.ConstructionTier
	}{
		{false, false, false, domain.TierSecure},
		{false, true, false, domain.TierSecure},
		{false, false, true, domain.TierSecure},
		{false, true, true, domain.TierComfort},
		{true, false, false, domain.TierSurvive},
		{true, true, true, domain.TierSurvive},
	} {
		if got := DefenseBuildTier(tc.promoted, tc.ring, tc.roamer); got != tc.want {
			t.Errorf("%+v: tier = %v", tc, got)
		}
	}
}

func TestDefenseRetiers(t *testing.T) {
	wall := func(x int32) domain.Building {
		b, err := domain.NewBuilding("Wall", domain.Cell{X: x, Z: 0}, domain.North, "BlocksGranite")
		if err != nil {
			t.Fatal(err)
		}
		return b
	}
	site := func(id string, x int32, tier domain.Fact[domain.ConstructionTier]) ConstructionSite {
		return ConstructionSite{ID: id, Building: wall(x), Stage: "blueprint", Tier: tier}
	}
	sites := []ConstructionSite{
		site("b", 2, domain.Known(domain.TierSecure)),
		site("a", 1, domain.Known(domain.TierComfort)),
		site("done", 3, domain.Known(domain.TierSurvive)),
		site("untiered", 4, domain.Unknown[domain.ConstructionTier]()),
		site("other", 9, domain.Known(domain.TierSecure)),
	}
	got := DefenseRetiers(sites, []domain.Building{wall(1), wall(2), wall(3), wall(4)})
	if len(got) != 2 || got[0].ID != "a" || got[1].ID != "b" {
		t.Fatalf("retiers = %+v", got)
	}
}
