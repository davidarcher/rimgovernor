package policy

import (
	"encoding/xml"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type xmlSparringDef struct {
	DefName string   `xml:"defName"`
	Trade   string   `xml:"tradeability"`
	Recipes string   `xml:"recipeMaker"`
	Tags    string   `xml:"weaponTags"`
	Verbs   []string `xml:"verbs>li>verbClass"`
	Tools   []struct {
		Capacities []string `xml:"capacities>li"`
		Power      string   `xml:"power"`
	} `xml:"tools>li"`
	Extensions []struct {
		Class   string `xml:"Class,attr"`
		Tier    string `xml:"tier"`
		Ceiling string `xml:"skillCeiling"`
		Gate    string `xml:"gateResearch"`
	} `xml:"modExtensions>li"`
}

func readSparringDefs(t *testing.T) map[string]xmlSparringDef {
	t.Helper()
	path := filepath.Join("..", "..", "..", "integrations", "rimgovernor-native", "Defs", "ThingDefs", "SparringRing.xml")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	var doc struct {
		Things []xmlSparringDef `xml:"ThingDef"`
	}
	if err := xml.Unmarshal(raw, &doc); err != nil {
		t.Fatal(err)
	}
	out := map[string]xmlSparringDef{}
	for _, d := range doc.Things {
		out[d.DefName] = d
	}
	return out
}

// The tier table lives on the weapon defs; every field of the Go mirror equals
// the XML, and every weapon is melee-only (one limb tool, no verbs) and cannot
// be crafted, traded or tagged.
func TestSparringTiersMirrorTheNativeWeaponDefs(t *testing.T) {
	defs := readSparringDefs(t)
	if len(defs) != len(SparringTiers) {
		t.Fatalf("%d sparring defs, %d Go rows", len(defs), len(SparringTiers))
	}
	for i, want := range SparringTiers {
		d, ok := defs[want.Weapon]
		if !ok {
			t.Fatal("weapon def missing from the native mod", want.Weapon)
		}
		if len(d.Extensions) != 1 || d.Extensions[0].Class != "RimGovernor.Runtime.SparringTier" {
			t.Fatal("weapon lacks exactly one SparringTier extension", want.Weapon)
		}
		e := d.Extensions[0]
		tier, _ := strconv.Atoi(e.Tier)
		ceil, _ := strconv.Atoi(e.Ceiling)
		if tier != i || ceil != want.Ceiling || strings.TrimSpace(e.Gate) != want.Gate {
			t.Fatalf("%s drifted from the Go table: xml %+v, go %+v", want.Weapon, e, want)
		}
		if len(d.Tools) != 1 || len(d.Tools[0].Capacities) != 1 || d.Tools[0].Capacities[0] != want.DamageType {
			t.Fatal("weapon is not one tool of the table's damage type", want.Weapon, d.Tools)
		}
		if power, _ := strconv.Atoi(d.Tools[0].Power); power != want.Power {
			t.Fatal("tool power drifted from the Go table", want.Weapon, d.Tools[0].Power)
		}
		if len(d.Verbs) != 0 {
			t.Fatal("weapon has verbs", want.Weapon)
		}
		if d.Trade != "None" || d.Recipes != "" || d.Tags != "" {
			t.Fatal("weapon is craftable, tradeable or tagged", want.Weapon)
		}
	}
}

// Damage is flat per damage type and the ceilings climb to the vanilla maximum.
func TestSparringTierTableIsConsistent(t *testing.T) {
	if SparringTiers[0].Gate != "" {
		t.Fatal("tier 0 has no gate", SparringTiers[0])
	}
	power := map[string]int{}
	for i, tier := range SparringTiers {
		if p, ok := power[tier.DamageType]; ok && p != tier.Power {
			t.Fatal("damage type has two powers", tier)
		}
		power[tier.DamageType] = tier.Power
		if i > 0 && tier.Ceiling <= SparringTiers[i-1].Ceiling {
			t.Fatal("ceilings must rise", i)
		}
	}
	if got := SparringTiers[len(SparringTiers)-1].Ceiling; got != 20 {
		t.Fatal("top ceiling is the vanilla maximum 20, got", got)
	}
}

func TestUnlockedSparringTierFollowsFinishedResearch(t *testing.T) {
	t.Parallel()
	known := func(done ...ResearchProjectID) domain.Fact[ResearchFacts] {
		return domain.Known(ResearchFacts{Finished: done})
	}
	for _, tc := range []struct {
		name     string
		research domain.Fact[ResearchFacts]
		ceiling  int
	}{
		{"unknown census holds at the club", domain.Unknown[ResearchFacts](), 8},
		{"nothing finished", known(), 8},
		{"unrelated research", known("Gunsmithing"), 8},
		{"smithing", known("Smithing"), 12},
		{"machining outranks smithing", known("Smithing", "Machining"), 16},
		{"fabrication is the best", known("Smithing", "Machining", "Fabrication"), 20},
		{"a higher gate alone still unlocks its tier", known("Fabrication"), 20},
	} {
		if got := UnlockedSparringTier(tc.research).Ceiling; got != tc.ceiling {
			t.Errorf("%s: ceiling %d, want %d", tc.name, got, tc.ceiling)
		}
	}
}
