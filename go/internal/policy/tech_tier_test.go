package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestSelectTechTierTable(t *testing.T) {
	ids := func(names ...string) domain.Fact[[]ResearchProjectID] {
		out := make([]ResearchProjectID, 0, len(names))
		for _, n := range names {
			out = append(out, ResearchProjectID(n))
		}
		return domain.Known(out)
	}
	cases := []struct {
		name     string
		finished domain.Fact[[]ResearchProjectID]
		faction  string
		tier     TechTier
		evidence string
	}{
		{"tribe, nothing finished", ids(), "Neolithic", TechTierCamp, ""},
		{"tribe, stonecutting", ids("Stonecutting"), "Neolithic", TechTierMasonry, "Stonecutting"},
		{"tribe, electricity without stonecutting stays camp", ids("Electricity"), "Neolithic", TechTierCamp, ""},
		{"tribe, stonecutting and electricity", ids("Electricity", "Stonecutting"), "Neolithic", TechTierPowered, "Electricity"},
		{"tribe, machining without electricity stays masonry", ids("Stonecutting", "Machining"), "Neolithic", TechTierMasonry, "Stonecutting"},
		{"tribe, up to fabrication", ids("Stonecutting", "Electricity", "Fabrication"), "Neolithic", TechTierIndustrial, "Fabrication"},
		{"tribe, advanced fabrication", ids("Stonecutting", "Electricity", "Machining", "AdvancedFabrication"), "Neolithic", TechTierSpacer, "AdvancedFabrication"},
		{"animal faction is camp", ids(), "Animal", TechTierCamp, ""},
		{"medieval floor is masonry", ids(), "Medieval", TechTierMasonry, "faction Medieval"},
		{"medieval with electricity is powered", ids("Electricity"), "Medieval", TechTierPowered, "Electricity"},
		{"industrial floor is industrial", ids(), "Industrial", TechTierIndustrial, "faction Industrial"},
		{"industrial with advanced fabrication is spacer", ids("AdvancedFabrication"), "Industrial", TechTierSpacer, "AdvancedFabrication"},
		{"spacer floor", ids(), "Spacer", TechTierSpacer, "faction Spacer"},
		{"ultra floor", ids(), "Ultra", TechTierSpacer, "faction Ultra"},
		{"archotech floor", ids(), "Archotech", TechTierSpacer, "faction Archotech"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got := SelectTechTier(tc.finished, domain.Known(tc.faction))
			tier, known := got.Value()
			if !known || tier != tc.tier {
				t.Fatalf("tier = %v (known %v), want %v", tier, known, tc.tier)
			}
			if again, _ := SelectTechTier(tc.finished, domain.Known(tc.faction)).Value(); again != tier {
				t.Fatalf("identical inputs gave %v then %v", tier, again)
			}
			if e := TechTierEvidence(tc.finished, domain.Known(tc.faction)); e != tc.evidence {
				t.Fatalf("evidence = %q, want %q", e, tc.evidence)
			}
		})
	}
}

func TestSelectTechTierUnknownInputs(t *testing.T) {
	finished := domain.Known([]ResearchProjectID{"Stonecutting", "Electricity", "Machining", "AdvancedFabrication"})
	for name, fact := range map[string]domain.Fact[TechTier]{
		"unknown research":  SelectTechTier(domain.Unknown[[]ResearchProjectID](), domain.Known("Industrial")),
		"unknown faction":   SelectTechTier(finished, domain.Unknown[string]()),
		"undefined faction": SelectTechTier(finished, domain.Known("Undefined")),
		"garbage faction":   SelectTechTier(finished, domain.Known("Tribal")),
	} {
		if _, known := fact.Value(); known {
			t.Fatalf("%s: tier known", name)
		}
	}
	if e := TechTierEvidence(finished, domain.Unknown[string]()); e != "" {
		t.Fatalf("evidence on unknown = %q", e)
	}
}

func TestSelectTechTierMonotone(t *testing.T) {
	projects := []ResearchProjectID{"Stonecutting", "Electricity", "Machining", "Fabrication", "AdvancedFabrication", "Batteries", "Devilstrand"}
	for _, faction := range []string{"Animal", "Neolithic", "Medieval", "Industrial", "Spacer", "Ultra", "Archotech"} {
		// Every subset, in every order the bits enumerate: adding a project
		// to any set never lowers the tier.
		for mask := 0; mask < 1<<len(projects); mask++ {
			var set []ResearchProjectID
			for i, p := range projects {
				if mask&(1<<i) != 0 {
					set = append(set, p)
				}
			}
			base, _ := SelectTechTier(domain.Known(set), domain.Known(faction)).Value()
			for i, p := range projects {
				if mask&(1<<i) != 0 {
					continue
				}
				more, _ := SelectTechTier(domain.Known(append(append([]ResearchProjectID{}, set...), p)), domain.Known(faction)).Value()
				if more < base {
					t.Fatalf("%s: adding %s to %v lowered %v to %v", faction, p, set, base, more)
				}
			}
		}
	}
}

func TestTechTierString(t *testing.T) {
	for tier, want := range map[TechTier]string{TechTierCamp: "Camp", TechTierMasonry: "Masonry", TechTierPowered: "Powered", TechTierIndustrial: "Industrial", TechTierSpacer: "Spacer", TechTier(9): "Camp"} {
		if got := tier.String(); got != want {
			t.Fatalf("%d.String() = %q, want %q", tier, got, want)
		}
	}
}
