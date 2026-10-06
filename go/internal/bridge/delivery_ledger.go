package bridge

import o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"

// MaxDeliveryRows bounds the keyed rows of the delivery ledger; native folds
// later keys into the overflow row.
const MaxDeliveryRows = 512

// ValidDeliveryLedger reports whether a delivery ledger is well formed at the
// read's tick. A ledger is derived measurement, so the colony read does not fail
// on a bad one: the decoder treats it as unknown instead.
func ValidDeliveryLedger(f *o.DeliveryLedgerFacts, tick int64) bool {
	if f == nil || validID(f.GetEpoch()) != nil || len(f.Rows) > MaxDeliveryRows {
		return false
	}
	seen := map[[3]string]bool{}
	for _, row := range f.Rows {
		kind := row.GetSourceKind()
		if row == nil || kind < o.DeliverySourceKind_DELIVERY_SOURCE_KIND_CROP || kind > o.DeliverySourceKind_DELIVERY_SOURCE_KIND_ANIMAL_PRODUCT || !validDeliveryCounters(row, tick) ||
			validID(row.GetSourceId()) != nil || validID(row.GetDefName()) != nil {
			return false
		}
		key := [3]string{kind.String(), row.GetSourceId(), row.GetDefName()}
		if seen[key] {
			return false
		}
		seen[key] = true
	}
	if f.Other != nil && (f.Other.SourceKind != nil || !validDeliveryCounters(f.Other, tick)) {
		return false
	}
	return f.GetLost() == 0 || f.Other != nil
}

func validDeliveryCounters(row *o.DeliveryRow, tick int64) bool {
	return row.Units != nil && row.GetUnits() >= 0 && row.Nutrition != nil && combatNumber(row.Nutrition, true) &&
		row.LastTick != nil && row.GetLastTick() >= 0 && row.GetLastTick() <= tick
}
