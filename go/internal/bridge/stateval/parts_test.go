package stateval

import (
	"testing"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

func anyPart(p *d.StatPartAny) *d.Opt_StatPartAny { return &d.Opt_StatPartAny{Value: p} }

func qualityRow() *d.StatPart_Quality {
	return &d.StatPart_Quality{
		FactorAwful: 0.5, FactorPoor: 0.75, FactorNormal: 1, FactorGood: 1.25, FactorExcellent: 1.5,
		FactorMasterwork: 2, FactorLegendary: 3,
		MaxGainAwful: 9999999, MaxGainPoor: 9999999, MaxGainNormal: 9999999, MaxGainGood: 9999999,
		MaxGainExcellent: 9999999, MaxGainMasterwork: 9999999, MaxGainLegendary: 4,
	}
}

func q(c int32) Subject {
	s := ThingSubject("Apparel_Parka", "")
	s.Quality = &c
	return s
}

func TestPartQuality(t *testing.T) {
	for _, c := range []struct {
		name     string
		base     float32
		negative bool
		quality  int32
		want     float32
	}{
		{"awful", 10, false, 0, 5},
		{"normal", 10, false, 2, 10},
		{"good", 10, false, 3, 12.5},
		{"legendary is capped by maxGain", 10, false, 6, 14},
		{"legendary under the cap", 1, false, 6, 3},
		{"a negative value is left alone", -10, false, 0, -10},
		{"a negative value scaled when applied", -10, true, 0, -5},
	} {
		t.Run(c.name, func(t *testing.T) {
			r := newRig(t, func(stat *d.StatDef, parka, _ *d.ThingDef) {
				row := qualityRow()
				row.ApplyToNegativeValues = c.negative
				stat.Parts = []*d.Opt_StatPartAny{anyPart(&d.StatPartAny{Value: &d.StatPartAny_StatPart_Quality{StatPart_Quality: row}})}
				parka.StatBases = append(parka.StatBases, mod(testStat, c.base))
			})
			if got := r.value(q(c.quality)); got != c.want {
				t.Errorf("= %v, want %v", got, c.want)
			}
		})
	}
}

func TestPartQualityOffset(t *testing.T) {
	for _, c := range []struct {
		name string
		defs []string
		qual int32
		want float32
	}{
		{"every def when the list is empty", nil, 3, 12},
		{"listed def", []string{"Apparel_Parka"}, 3, 12},
		{"unlisted def", []string{"Steel"}, 3, 10},
		{"normal offset", nil, 2, 10},
	} {
		t.Run(c.name, func(t *testing.T) {
			r := newRig(t, func(stat *d.StatDef, parka, _ *d.ThingDef) {
				row := &d.StatPart_Quality_Offset{OffsetGood: 2, ThingDefs: c.defs}
				stat.Parts = []*d.Opt_StatPartAny{anyPart(&d.StatPartAny{Value: &d.StatPartAny_StatPart_Quality_Offset{StatPart_Quality_Offset: row}})}
				parka.StatBases = append(parka.StatBases, mod(testStat, 10))
			})
			if got := r.value(q(c.qual)); got != c.want {
				t.Errorf("= %v, want %v", got, c.want)
			}
		})
	}
}

func TestPartStuffAddsPowerTimesMultiplier(t *testing.T) {
	const power, multiplier = "StuffPower_Armor_Sharp", "StuffEffectMultiplierArmor"
	r := newRig(t, func(stat *d.StatDef, parka, _ *d.ThingDef) {
		row := &d.StatPart_Stuff{StuffPowerStat: power, MultiplierStat: multiplier}
		stat.Parts = []*d.Opt_StatPartAny{anyPart(&d.StatPartAny{Value: &d.StatPartAny_StatPart_Stuff{StatPart_Stuff: row}})}
		parka.StatBases = append(parka.StatBases, mod(testStat, 10))
	})
	p, err := r.eval.Value(power, ThingSubject("Steel", ""))
	if err != nil {
		t.Fatal(err)
	}
	m, err := r.eval.Value(multiplier, ThingSubject("Apparel_Parka", ""))
	if err != nil {
		t.Fatal(err)
	}
	if p == 0 || m == 0 {
		t.Fatalf("degenerate sample: power %v, multiplier %v", p, m)
	}
	if got := r.value(ThingSubject("Apparel_Parka", "")); got != 10 {
		t.Errorf("no stuff = %v, want the base 10", got)
	}
	if got, want := r.value(ThingSubject("Apparel_Parka", "Steel")), float32(10+float32(m*p)); got != want {
		t.Errorf("steel = %v, want %v", got, want)
	}
}

func TestPartHyperlinksLeavesTheValue(t *testing.T) {
	r := newRig(t, func(stat *d.StatDef, parka, _ *d.ThingDef) {
		row := &d.StatPart_Hyperlinks{ThingDefs: []string{"Steel"}}
		stat.Parts = []*d.Opt_StatPartAny{anyPart(&d.StatPartAny{Value: &d.StatPartAny_StatPart_Hyperlinks{StatPart_Hyperlinks: row}})}
		parka.StatBases = append(parka.StatBases, mod(testStat, 10))
	})
	if got := r.value(ThingSubject("Apparel_Parka", "")); got != 10 {
		t.Errorf("= %v, want 10", got)
	}
}
