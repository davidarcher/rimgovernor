package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestSilverGap(t *testing.T) {
	need := domain.Known(TradeNeed{MedicineReplenish: 10}) // 180 silver
	colonists := domain.Known[int64](3)                    // reserve 300
	for _, tc := range []struct {
		name   string
		need   domain.Fact[TradeNeed]
		silver int64
		want   float64
	}{
		{"a silver need", need, 100, 380},
		{"silver covers price plus reserve", need, 480, 0},
		{"surplus only is no purchase", domain.Known(TradeNeed{Surplus: []Amount{{Resource: "Steel", Count: 9}}}), 0, 0},
		{"shortfall counts", domain.Known(TradeNeed{Shortfall: []Amount{{Resource: "Steel", Count: 100}}}), 400, 0},
	} {
		got, known := SilverGap(CoreItemFacts(), tc.need, domain.Known(tc.silver), colonists).Value()
		if !known {
			t.Errorf("%s: unknown", tc.name)
			continue
		}
		if tc.name == "shortfall counts" {
			if got <= 0 {
				t.Errorf("%s: gap %v", tc.name, got)
			}
			continue
		}
		if got != tc.want {
			t.Errorf("%s: gap %v want %v", tc.name, got, tc.want)
		}
	}
	if _, known := SilverGap(CoreItemFacts(), need, domain.Unknown[int64](), colonists).Value(); known {
		t.Error("unknown silver is a known gap")
	}
	short, _ := SilverShort(CoreItemFacts(), need, domain.Known[int64](100), colonists).Value()
	if !short {
		t.Error("a positive gap is short")
	}
}
