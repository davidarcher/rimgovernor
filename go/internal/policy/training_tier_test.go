package policy

import (
	"encoding/xml"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
)

type xmlTierDef struct {
	DefName    string `xml:"defName"`
	Parent     string `xml:"ParentName,attr"`
	Projectile string `xml:"verbs>li>defaultProjectile"`
	Warmup     string `xml:"verbs>li>warmupTime"`
	Cooldown   string `xml:"statBases>RangedWeapon_Cooldown"`
	Trade      string `xml:"tradeability"`
	Recipes    string `xml:"recipeMaker"`
	Tags       string `xml:"weaponTags"`
	Extensions []struct {
		Class      string `xml:"Class,attr"`
		Tier       string `xml:"tier"`
		Multiplier string `xml:"multiplier"`
		XPPerShot  string `xml:"xpPerShot"`
		Ceiling    string `xml:"skillCeiling"`
		Gate       string `xml:"gateResearch"`
	} `xml:"modExtensions>li"`
	Damage string `xml:"projectile>damageAmountBase"`
}

func readThingDefs(t *testing.T) map[string]xmlTierDef {
	t.Helper()
	path := filepath.Join("..", "..", "..", "integrations", "rimgovernor-native", "Defs", "ThingDefs", "TrainingRange.xml")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	var doc struct {
		Things []xmlTierDef `xml:"ThingDef"`
	}
	if err := xml.Unmarshal(raw, &doc); err != nil {
		t.Fatal(err)
	}
	out := map[string]xmlTierDef{}
	for _, d := range doc.Things {
		out[d.DefName] = d
	}
	return out
}

// The tier table lives on the weapon defs; every field of the Go mirror equals
// the XML, and every weapon shares the 3 s cycle, issues only its own projectile
// at 1 damage and cannot be crafted, traded or tagged.
func TestTrainingTiersMirrorTheNativeWeaponDefs(t *testing.T) {
	defs := readThingDefs(t)
	for i, want := range TrainingTiers {
		d, ok := defs[want.Weapon]
		if !ok {
			t.Fatal("weapon def missing from the native mod", want.Weapon)
		}
		if len(d.Extensions) != 1 || d.Extensions[0].Class != "RimGovernor.Runtime.TrainingTier" {
			t.Fatal("weapon lacks exactly one TrainingTier extension", want.Weapon)
		}
		e := d.Extensions[0]
		mult, _ := strconv.ParseFloat(e.Multiplier, 64)
		xp, _ := strconv.Atoi(e.XPPerShot)
		ceil, _ := strconv.Atoi(e.Ceiling)
		tier, _ := strconv.Atoi(e.Tier)
		if tier != i || mult != want.Multiplier || xp != want.XPPerShot || ceil != want.Ceiling || strings.TrimSpace(e.Gate) != want.Gate {
			t.Fatalf("%s drifted from the Go table: xml %+v, go %+v", want.Weapon, e, want)
		}
		warmup, _ := strconv.ParseFloat(d.Warmup, 64)
		cooldown, _ := strconv.ParseFloat(d.Cooldown, 64)
		if cycle := warmup + cooldown; cycle < 2.999 || cycle > 3.001 {
			t.Fatal("cycle is not 3 s", want.Weapon, cycle)
		}
		if d.Trade != "None" || d.Recipes != "" || d.Tags != "" {
			t.Fatal("weapon is craftable, tradeable or tagged", want.Weapon)
		}
		if d.Projectile != want.Projectile {
			t.Fatal("weapon fires the wrong projectile", want.Weapon, d.Projectile)
		}
		p, ok := defs[want.Projectile]
		if !ok || p.Damage != "1" {
			t.Fatal("projectile missing or not exactly 1 damage", want.Projectile, p.Damage)
		}
	}
}

// XP per shot is the tier-0 XP times the multiplier, and the ceilings climb.
func TestTrainingTierTableIsConsistent(t *testing.T) {
	base := TrainingTiers[0]
	if base.Ceiling != TrainingSkillTarget || base.Gate != "" {
		t.Fatal("tier 0 is the bow with no gate at the existing skill target", base)
	}
	for i, tier := range TrainingTiers {
		if got := int(float64(base.XPPerShot) * tier.Multiplier); got != tier.XPPerShot {
			t.Fatal("xp per shot is not base x multiplier", i, got, tier.XPPerShot)
		}
		if i > 0 && tier.Ceiling <= TrainingTiers[i-1].Ceiling {
			t.Fatal("ceilings must rise", i)
		}
	}
}
