package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func gearRow(line, thing, zone string, hp, sell float64) TradeSheetRowFact {
	row := tradeRow(line, "Apparel_Parka", 1, 0, 0, sell)
	row.ThingID, row.ZoneID, row.HitPoints, row.HitPointsKnown = thing, zone, hp, true
	row.ProtectedExport, row.ProtectedExportKnown = true, true
	return row
}

// Worn-dump gear above the incinerator's hit-point cap sells; gear at or below
// it burns, and gear outside the worn dump is never sold.
func TestSaleGearIsTheWornDumpAboveTheIncineratorCap(t *testing.T) {
	rows := []TradeSheetRowFact{
		gearRow("#0", "Thing_Poor", "Zone_worn", 0.9, 40),
		gearRow("#1", "Thing_Burns", "Zone_worn", domain.GearHitPointFloor, 10),
		gearRow("#2", "Thing_Armory", "Zone_armory", 1, 90),
		gearRow("#3", "Thing_Field", "", 1, 90),
		tradeRow("#4", "Silver", 500, 1000, 1, 1),
	}
	sale := SaleGear(rows, map[string]bool{"Zone_worn": true})
	if want := map[string]bool{"Thing_Poor": true}; !reflect.DeepEqual(sale, want) {
		t.Fatalf("sale = %v, want %v", sale, want)
	}
	facts := tradeFacts(rows, 500, 1000, 500)
	facts.SaleGear = sale
	// A trader is present, no catalog target: only the gear sells, past the
	// native gear protection the accept authorizes.
	s := SelectTrade(domain.TradeEconomicPolicy{}, facts)
	if want := []TradeSelectionLine{{"#0", "Apparel_Parka", -1}}; s.Refused || !reflect.DeepEqual(s.Selected, want) {
		t.Fatalf("selected = %+v (%s), want %+v", s.Selected, s.Reason, want)
	}
	facts.SaleGear = nil
	if s = SelectTrade(domain.TradeEconomicPolicy{}, facts); len(s.Selected) != 0 {
		t.Fatalf("no sale gear sold %+v", s.Selected)
	}
}
