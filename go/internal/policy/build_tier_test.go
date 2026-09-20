package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestSelectBuildTierTable(t *testing.T) {
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
		tier     BuildTier
		evidence string
	}{
		{"tribe, nothing finished", ids(), "Neolithic", BuildTierCamp, ""},
		{"tribe, stonecutting", ids("Stonecutting"), "Neolithic", BuildTierMasonry, "Stonecutting"},
		{"tribe, electricity without stonecutting stays camp", ids("Electricity"), "Neolithic", BuildTierCamp, ""},
		{"tribe, stonecutting and electricity", ids("Electricity", "Stonecutting"), "Neolithic", BuildTierPowered, "Electricity"},
		{"tribe, machining without electricity stays masonry", ids("Stonecutting", "Machining"), "Neolithic", BuildTierMasonry, "Stonecutting"},
		{"tribe, up to fabrication", ids("Stonecutting", "Electricity", "Fabrication"), "Neolithic", BuildTierIndustrial, "Fabrication"},
		{"tribe, advanced fabrication", ids("Stonecutting", "Electricity", "Machining", "AdvancedFabrication"), "Neolithic", BuildTierSpacer, "AdvancedFabrication"},
		{"animal faction is camp", ids(), "Animal", BuildTierCamp, ""},
		{"medieval floor is masonry", ids(), "Medieval", BuildTierMasonry, "faction Medieval"},
		{"medieval with electricity is powered", ids("Electricity"), "Medieval", BuildTierPowered, "Electricity"},
		{"industrial floor is industrial", ids(), "Industrial", BuildTierIndustrial, "faction Industrial"},
		{"industrial with advanced fabrication is spacer", ids("AdvancedFabrication"), "Industrial", BuildTierSpacer, "AdvancedFabrication"},
		{"spacer floor", ids(), "Spacer", BuildTierSpacer, "faction Spacer"},
		{"ultra floor", ids(), "Ultra", BuildTierSpacer, "faction Ultra"},
		{"archotech floor", ids(), "Archotech", BuildTierSpacer, "faction Archotech"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got := SelectBuildTier(tc.finished, domain.Known(tc.faction))
			tier, known := got.Value()
			if !known || tier != tc.tier {
				t.Fatalf("tier = %v (known %v), want %v", tier, known, tc.tier)
			}
			if again, _ := SelectBuildTier(tc.finished, domain.Known(tc.faction)).Value(); again != tier {
				t.Fatalf("identical inputs gave %v then %v", tier, again)
			}
			if e := BuildTierEvidence(tc.finished, domain.Known(tc.faction)); e != tc.evidence {
				t.Fatalf("evidence = %q, want %q", e, tc.evidence)
			}
		})
	}
}

func TestSelectBuildTierUnknownInputs(t *testing.T) {
	finished := domain.Known([]ResearchProjectID{"Stonecutting", "Electricity", "Machining", "AdvancedFabrication"})
	for name, fact := range map[string]domain.Fact[BuildTier]{
		"unknown research":  SelectBuildTier(domain.Unknown[[]ResearchProjectID](), domain.Known("Industrial")),
		"unknown faction":   SelectBuildTier(finished, domain.Unknown[string]()),
		"undefined faction": SelectBuildTier(finished, domain.Known("Undefined")),
		"garbage faction":   SelectBuildTier(finished, domain.Known("Tribal")),
	} {
		if _, known := fact.Value(); known {
			t.Fatalf("%s: tier known", name)
		}
	}
	if e := BuildTierEvidence(finished, domain.Unknown[string]()); e != "" {
		t.Fatalf("evidence on unknown = %q", e)
	}
}

func TestSelectBuildTierMonotone(t *testing.T) {
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
			base, _ := SelectBuildTier(domain.Known(set), domain.Known(faction)).Value()
			for i, p := range projects {
				if mask&(1<<i) != 0 {
					continue
				}
				more, _ := SelectBuildTier(domain.Known(append(append([]ResearchProjectID{}, set...), p)), domain.Known(faction)).Value()
				if more < base {
					t.Fatalf("%s: adding %s to %v lowered %v to %v", faction, p, set, base, more)
				}
			}
		}
	}
}

func TestBuildTierString(t *testing.T) {
	for tier, want := range map[BuildTier]string{BuildTierCamp: "Camp", BuildTierMasonry: "Masonry", BuildTierPowered: "Powered", BuildTierIndustrial: "Industrial", BuildTierSpacer: "Spacer", BuildTier(9): "Camp"} {
		if got := tier.String(); got != want {
			t.Fatalf("%d.String() = %q, want %q", tier, got, want)
		}
	}
}
