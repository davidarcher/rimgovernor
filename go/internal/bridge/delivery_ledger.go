package bridge

import o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"

// MaxDeliveryRows bounds the keyed rows of the delivery ledger; native folds
// later keys into the overflow row. MaxHuntRecords bounds each of the kill and
// butcher windows; native keeps the newest.
const (
	MaxDeliveryRows = 512
	MaxHuntRecords  = 256
)

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
	return (f.GetLost() == 0 || f.Other != nil) && validHuntRecords(f, tick)
}

// validHuntRecords checks the kill and butcher windows: each corpse appears once
// per window, counters are non-negative and no record is from the future.
func validHuntRecords(f *o.DeliveryLedgerFacts, tick int64) bool {
	if len(f.Kills) > MaxHuntRecords || len(f.Butchers) > MaxHuntRecords || f.GetKillsTotal() < uint64(len(f.Kills)) || f.GetButchersTotal() < uint64(len(f.Butchers)) {
		return false
	}
	killed := map[string]bool{}
	for _, k := range f.Kills {
		if k == nil || validID(k.GetCorpseId()) != nil || killed[k.GetCorpseId()] || validID(k.GetRace()) != nil || validID(k.GetPawnId()) != nil || k.BodySize == nil || !combatNumber(k.BodySize, true) ||
			k.PotentialNutrition == nil || !combatNumber(k.PotentialNutrition, true) || k.Tick == nil || k.GetTick() < 0 || k.GetTick() > tick {
			return false
		}
		killed[k.GetCorpseId()] = true
	}
	butchered := map[string]bool{}
	for _, b := range f.Butchers {
		if b == nil || validID(b.GetCorpseId()) != nil || butchered[b.GetCorpseId()] || validID(b.GetRecipe()) != nil || b.MeatUnits == nil || b.GetMeatUnits() < 0 ||
			b.MeatNutrition == nil || !combatNumber(b.MeatNutrition, true) || b.LeatherUnits == nil || b.GetLeatherUnits() < 0 || b.Tick == nil || b.GetTick() < 0 || b.GetTick() > tick ||
			b.GetMeatUnits() > 0 && validID(b.GetMeatDef()) != nil {
			return false
		}
		butchered[b.GetCorpseId()] = true
	}
	return true
}

func validDeliveryCounters(row *o.DeliveryRow, tick int64) bool {
	return row.Units != nil && row.GetUnits() >= 0 && row.Nutrition != nil && combatNumber(row.Nutrition, true) &&
		row.LastTick != nil && row.GetLastTick() >= 0 && row.GetLastTick() <= tick
}
