package policy

import (
	"encoding/xml"
	"os"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type xmlSparringDef struct {
	DefName string   `xml:"defName"`
	Name    string   `xml:"Name,attr"`
	Parent  string   `xml:"ParentName,attr"`
	Trade   string   `xml:"tradeability"`
	Recipes string   `xml:"recipeMaker"`
	Tags    string   `xml:"weaponTags"`
	Verbs   []string `xml:"verbs>li>verbClass"`
	// Practice apparel (#2706) fields.
	Categories []string `xml:"thingCategories>li"`
	Mass       string   `xml:"statBases>Mass"`
	Sharp      string   `xml:"statBases>ArmorRating_Sharp"`
	Blunt      string   `xml:"statBases>ArmorRating_Blunt"`
	Wear       struct {
		Groups []string `xml:"bodyPartGroups>li"`
		Layers []string `xml:"layers>li"`
		Tags   []string `xml:"tags>li"`
		Outfit []string `xml:"defaultOutfitTags>li"`
	} `xml:"apparel"`
	Tools []struct {
		Capacities []string `xml:"capacities>li"`
		Power      string   `xml:"power"`
	} `xml:"tools>li"`
	Extensions []struct {
		Class   string   `xml:"Class,attr"`
		Tier    string   `xml:"tier"`
		Ceiling string   `xml:"skillCeiling"`
		Gate    string   `xml:"gateResearch"`
		Apparel []string `xml:"apparel>li"`
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
		if d.DefName == "" {
			d.DefName = d.Name
		}
		out[d.DefName] = d
	}
	return out
}

// The tier table lives on the weapon defs; every field of the Go mirror equals
// the XML, and every weapon is melee-only (one limb tool, no verbs) and cannot
// be crafted, traded or tagged.
func TestSparringTiersMirrorTheNativeWeaponDefs(t *testing.T) {
	defs := readSparringDefs(t)
	weapons := 0
	for _, d := range defs {
		if len(d.Extensions) > 0 {
			weapons++
		}
	}
	if weapons != len(SparringTiers) {
		t.Fatalf("%d sparring weapon defs, %d Go rows", weapons, len(SparringTiers))
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

// The practice apparel (#2706): each tier's set is the Go row's list, every piece
// is untradeable, uncraftable and untagged (so no outfit or optimizer reaches it),
// and the set wears together without leaving a pawn nude. Core has no Hands layer,
// so gloves share Middle with the vest; they must not share a body-part group.
func TestSparringApparelMirrorsTheNativeDefs(t *testing.T) {
	defs := readSparringDefs(t)
	type piece struct {
		groups, layer, sharp, blunt string
	}
	want := map[string]piece{
		practiceTunic:  {"Torso,Legs", "OnSkin", "", ""},
		practiceHelmet: {"FullHead", "Overhead", "2.2", "2.2"},
		practiceGloves: {"Hands", "Middle", "2.2", "2.2"},
		practiceVest:   {"Torso", "Middle", "0.8", "0.5"},
	}
	for i, tier := range SparringTiers {
		e := defs[tier.Weapon].Extensions[0]
		if !slices.Equal(e.Apparel, tier.Apparel) {
			t.Fatalf("%s apparel drifted: xml %v, go %v", tier.Weapon, e.Apparel, tier.Apparel)
		}
		if !slices.Contains(tier.Apparel, practiceTunic) {
			t.Fatal("tier lacks the tunic, so a swapped pawn would be nude", i)
		}
		if slices.Contains(tier.Apparel, practiceHelmet) != (i >= 1) ||
			slices.Contains(tier.Apparel, practiceGloves) != (i >= 1) ||
			slices.Contains(tier.Apparel, practiceVest) != (i >= 2) {
			t.Fatal("helmet and gloves from tier 1, vest from tier 2", i)
		}
		seen := map[string]string{} // layer/group -> piece
		for _, name := range tier.Apparel {
			d, ok := defs[name]
			if !ok {
				t.Fatal("apparel def missing from the native mod", name)
			}
			// Mass and tradeability come from the abstract practice base.
			if base, ok := defs[d.Parent]; ok {
				d.Mass, d.Trade = base.Mass, base.Trade
			}
			w := want[name]
			if got := strings.Join(d.Wear.Groups, ","); got != w.groups {
				t.Fatalf("%s covers %s, want %s", name, got, w.groups)
			}
			if len(d.Wear.Layers) != 1 || d.Wear.Layers[0] != w.layer {
				t.Fatal("wrong layer", name, d.Wear.Layers)
			}
			if d.Sharp != w.sharp || d.Blunt != w.blunt {
				t.Fatalf("%s armor drifted: sharp %q blunt %q", name, d.Sharp, d.Blunt)
			}
			if mass, _ := strconv.ParseFloat(d.Mass, 64); mass <= 0 || mass > 1 {
				t.Fatal("practice apparel is about a kilogram", name, d.Mass)
			}
			if d.Trade != "None" || d.Recipes != "" || len(d.Categories) != 0 || len(d.Wear.Tags) != 0 || len(d.Wear.Outfit) != 0 {
				t.Fatal("apparel is craftable, tradeable, categorized or tagged", name)
			}
			for _, g := range d.Wear.Groups {
				key := d.Wear.Layers[0] + "/" + g
				if other, clash := seen[key]; clash {
					t.Fatalf("%s and %s share %s and cannot be worn together", name, other, key)
				}
				seen[key] = name
			}
		}
		var torso, legs bool
		for _, name := range tier.Apparel {
			torso = torso || slices.Contains(defs[name].Wear.Groups, "Torso")
			legs = legs || slices.Contains(defs[name].Wear.Groups, "Legs")
		}
		if !torso || !legs {
			t.Fatal("PsychologicallyNude needs a Torso and a Legs piece", i)
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
