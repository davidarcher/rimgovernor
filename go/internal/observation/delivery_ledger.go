package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// DeliveryKind is the production kind a delivery was counted under. The
// source-to-channel mapping is the consumer's, from its own census.
type DeliveryKind string

const (
	DeliveryCrop          DeliveryKind = "crop"
	DeliveryFish          DeliveryKind = "fish"
	DeliveryForage        DeliveryKind = "forage"
	DeliveryAnimalProduct DeliveryKind = "animal_product"
)

// DeliveryKey identifies one counter: a growing zone id (crop), a water body's
// root cell "x,z" (fish), a plant def (forage) or an animal race (animal
// product), with the delivered def.
type DeliveryKey struct {
	Kind     DeliveryKind
	SourceID string
	Def      string
}

// DeliveryCount is a cumulative counter within one DeliveryLedger load token.
type DeliveryCount struct {
	Units, LastTick int64
	Nutrition       float64
}

// Kill is a player pawn's kill of an animal, keyed by its corpse's thing id (the
// food census's stock id). PotentialNutrition is the meat it would yield.
type Kill struct {
	CorpseID, Race               string
	BodySize, PotentialNutrition float64
	Tick                         int64
}

// Butcher is one butcher recipe's products from a corpse, counted where they
// are made; a reader links it to its Kill by CorpseID.
type Butcher struct {
	CorpseID, Recipe, MeatDef string
	MeatUnits, LeatherUnits   int64
	MeatNutrition             float64
	Tick                      int64
}

// DeliveryLedger is native's cumulative production-site delivery counters.
// A reader diffs successive ledgers of one load token and re-baselines when the
// token changes (a load or restart). Other and Lost summarize deliveries beyond
// the key bound. Kills and Butchers are the newest records in tick order;
// KillsTotal and ButchersTotal count every record of the load token, so a
// reader knows when records fell off the window.
type DeliveryLedger struct {
	LoadToken                 string
	Counts                    map[DeliveryKey]DeliveryCount
	Other                     DeliveryCount
	Lost                      uint64
	Kills                     []Kill
	Butchers                  []Butcher
	KillsTotal, ButchersTotal uint64
}

var deliveryKinds = map[o.DeliverySourceKind]DeliveryKind{
	o.DeliverySourceKind_DELIVERY_SOURCE_KIND_CROP:           DeliveryCrop,
	o.DeliverySourceKind_DELIVERY_SOURCE_KIND_FISH:           DeliveryFish,
	o.DeliverySourceKind_DELIVERY_SOURCE_KIND_FORAGE:         DeliveryForage,
	o.DeliverySourceKind_DELIVERY_SOURCE_KIND_ANIMAL_PRODUCT: DeliveryAnimalProduct,
}

// colonyDeliveryLedger is unknown for a missing, unavailable or malformed
// census, never an empty ledger: zero deliveries would read as a measured zero.
func colonyDeliveryLedger(section *o.DeliveryLedgerSection, tick int64) domain.Fact[DeliveryLedger] {
	f := section.GetObserved()
	if !bridge.ValidDeliveryLedger(f, tick) {
		return domain.Unknown[DeliveryLedger]()
	}
	r := DeliveryLedger{LoadToken: f.GetEpoch(), Counts: make(map[DeliveryKey]DeliveryCount, len(f.Rows)), Lost: f.GetLost(),
		KillsTotal: f.GetKillsTotal(), ButchersTotal: f.GetButchersTotal()}
	for _, k := range f.Kills {
		r.Kills = append(r.Kills, Kill{CorpseID: k.GetCorpseId(), Race: k.GetRace(), BodySize: k.GetBodySize(), PotentialNutrition: k.GetPotentialNutrition(), Tick: k.GetTick()})
	}
	for _, b := range f.Butchers {
		r.Butchers = append(r.Butchers, Butcher{CorpseID: b.GetCorpseId(), Recipe: b.GetRecipe(), MeatDef: b.GetMeatDef(), MeatUnits: b.GetMeatUnits(),
			MeatNutrition: b.GetMeatNutrition(), LeatherUnits: b.GetLeatherUnits(), Tick: b.GetTick()})
	}
	for _, row := range f.Rows {
		r.Counts[DeliveryKey{Kind: deliveryKinds[row.GetSourceKind()], SourceID: row.GetSourceId(), Def: row.GetDefName()}] = deliveryCount(row)
	}
	if f.Other != nil {
		r.Other = deliveryCount(f.Other)
	}
	return domain.Known(r)
}

func deliveryCount(row *o.DeliveryRow) DeliveryCount {
	return DeliveryCount{Units: row.GetUnits(), Nutrition: row.GetNutrition(), LastTick: row.GetLastTick()}
}
