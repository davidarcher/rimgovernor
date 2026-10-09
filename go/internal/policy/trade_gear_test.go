package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func gearRow(line, thing, zone string, hp float64, quality int32, sell float64) TradeSheetRowFact {
	row := tradeRow(line, "Apparel_Parka", 1, 0, 0, sell)
	row.ThingID, row.ZoneID, row.HitPoints, row.HitPointsKnown = thing, zone, hp, true
	row.Quality, row.QualityKnown = quality, true
	return row
}

// Warehouse gear below the hit-point or quality floor sells; gear at or above
// both floors (a full armory's overflow) and gear outside the warehouse do not.
func TestSaleGearIsWarehouseGearBelowTheKeepFloors(t *testing.T) {
	normal := int32(domain.QualityRank(domain.GearQualityFloor))
	worn := gearRow("#1", "Thing_Worn", "Zone_wh", domain.GearHitPointFloor-0.01, normal, 10)
	unknownQuality := gearRow("#6", "Thing_NoQuality", "Zone_wh", 0.1, normal, 10)
	unknownQuality.QualityKnown = false
	unknownHP := gearRow("#7", "Thing_NoHP", "Zone_wh", 0.1, normal-1, 10)
	unknownHP.HitPointsKnown = false
	rows := []TradeSheetRowFact{
		gearRow("#0", "Thing_Poor", "Zone_wh", 1, normal-1, 40),
		worn,
		gearRow("#2", "Thing_Kept", "Zone_wh", domain.GearHitPointFloor, normal, 90),
		gearRow("#3", "Thing_Armory", "Zone_armory", 0.1, 0, 90),
		gearRow("#4", "Thing_Field", "", 0.1, 0, 90),
		tradeRow("#5", "Silver", 500, 1000, 1, 1),
		unknownQuality, unknownHP,
	}
	sale := SaleGear(rows, map[string]bool{"Zone_wh": true})
	if want := map[string]bool{"Thing_Poor": true, "Thing_Worn": true}; !reflect.DeepEqual(sale, want) {
		t.Fatalf("sale = %v, want %v", sale, want)
	}
	facts := tradeFacts(rows, 500, 1000, 500)
	facts.SaleGear = sale
	// A trader is present, no catalog target: only the gear sells, past the
	// native gear protection the accept authorizes.
	s := SelectTrade(domain.TradeEconomicPolicy{}, facts)
	if want := []TradeSelectionLine{{"#0", "Apparel_Parka", -1}, {"#1", "Apparel_Parka", -1}}; s.Refused || !reflect.DeepEqual(s.Selected, want) {
		t.Fatalf("selected = %+v (%s), want %+v", s.Selected, s.Reason, want)
	}
	facts.SaleGear = nil
	if s = SelectTrade(domain.TradeEconomicPolicy{}, facts); len(s.Selected) != 0 {
		t.Fatalf("no sale gear sold %+v", s.Selected)
	}
}
