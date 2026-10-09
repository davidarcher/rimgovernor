package observation

import (
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func deliveryRow(kind o.DeliverySourceKind, source, def string, units int64, nutrition float64, tick int64) *o.DeliveryRow {
	return &o.DeliveryRow{SourceKind: &kind, SourceId: proto.String(source), DefName: proto.String(def), Units: proto.Int64(units), Nutrition: proto.Float64(nutrition), LastTick: proto.Int64(tick)}
}

func TestDeliveryLedgerDecodeAndUnknown(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	identity := Identity{Colony: "colony", Load: "load", Map: 0, Tick: 7, NativeGeneration: domain.Known(domain.NativeGeneration(1))}
	decode := func(ledger *o.DeliveryLedgerSection) domain.Fact[DeliveryLedger] {
		t.Helper()
		reply := &o.ColonyFactsReply{}
		if err := protojson.Unmarshal(data, reply); err != nil {
			t.Fatal(err)
		}
		reply.GetObserved().DeliveryLedger = ledger
		p, err := DecodeColony(reply, identity, bridge.Tables{})
		if err != nil {
			t.Fatal(err)
		}
		return p.DeliveryLedger
	}
	observed := func(f *o.DeliveryLedgerFacts) *o.DeliveryLedgerSection {
		return &o.DeliveryLedgerSection{Outcome: &o.DeliveryLedgerSection_Observed{Observed: f}}
	}
	crop, fish := o.DeliverySourceKind_DELIVERY_SOURCE_KIND_CROP, o.DeliverySourceKind_DELIVERY_SOURCE_KIND_FISH
	good := func() *o.DeliveryLedgerFacts {
		return &o.DeliveryLedgerFacts{Epoch: proto.String("e1"), Rows: []*o.DeliveryRow{
			deliveryRow(crop, "12", "RawRice", 9, 0.45, 5), deliveryRow(fish, "3,4", "Fish_Bass", 2, 1.1, 7)}}
	}

	ledger, known := decode(observed(good())).Value()
	if !known || ledger.LoadToken != "e1" || len(ledger.Counts) != 2 {
		t.Fatalf("decoded %+v known=%v", ledger, known)
	}
	if got := ledger.Counts[DeliveryKey{Kind: DeliveryFish, SourceID: "3,4", Def: "Fish_Bass"}]; got.Units != 2 || got.Nutrition != 1.1 || got.LastTick != 7 {
		t.Fatalf("fish counter %+v", got)
	}
	hunted := good()
	hunted.Kills = []*o.KillRecord{{CorpseId: proto.String("Corpse1"), PawnId: proto.String("Deer1"), Race: proto.String("Deer"), BodySize: proto.Float64(1.2), PotentialNutrition: proto.Float64(1.6), Tick: proto.Int64(4)}}
	hunted.Butchers = []*o.ButcherRecord{{CorpseId: proto.String("Corpse1"), Recipe: proto.String("ButcherCorpseFlesh"), MeatDef: proto.String("Meat_Deer"), MeatUnits: proto.Int64(60),
		MeatNutrition: proto.Float64(1.2), LeatherUnits: proto.Int64(20), Tick: proto.Int64(6)}}
	if l, known := decode(observed(hunted)).Value(); !known || len(l.Kills) != 1 || len(l.Butchers) != 1 ||
		l.Kills[0] != (Kill{CorpseID: "Corpse1", PawnID: "Deer1", Race: "Deer", BodySize: 1.2, PotentialNutrition: 1.6, Tick: 4}) ||
		l.Butchers[0] != (Butcher{CorpseID: "Corpse1", Recipe: "ButcherCorpseFlesh", MeatDef: "Meat_Deer", MeatUnits: 60, MeatNutrition: 1.2, LeatherUnits: 20, Tick: 6}) {
		t.Fatalf("hunt records %+v known=%v", l, known)
	}
	if empty, known := decode(observed(&o.DeliveryLedgerFacts{Epoch: proto.String("e1")})).Value(); !known || len(empty.Counts) != 0 {
		t.Fatalf("an observed empty ledger is a known empty ledger: %+v known=%v", empty, known)
	}

	unknown := map[string]*o.DeliveryLedgerSection{
		"missing":     nil,
		"unavailable": {Outcome: &o.DeliveryLedgerSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}},
		"no epoch":    observed(&o.DeliveryLedgerFacts{Rows: good().Rows}),
	}
	for name, mutate := range map[string]func(*o.DeliveryLedgerFacts){
		"unspecified kind":    func(f *o.DeliveryLedgerFacts) { f.Rows[0].SourceKind = nil },
		"negative units":      func(f *o.DeliveryLedgerFacts) { f.Rows[0].Units = proto.Int64(-1) },
		"missing units":       func(f *o.DeliveryLedgerFacts) { f.Rows[0].Units = nil },
		"negative food":       func(f *o.DeliveryLedgerFacts) { f.Rows[0].Nutrition = proto.Float64(-0.5) },
		"future tick":         func(f *o.DeliveryLedgerFacts) { f.Rows[0].LastTick = proto.Int64(8) },
		"empty source":        func(f *o.DeliveryLedgerFacts) { f.Rows[0].SourceId = proto.String("") },
		"duplicate key":       func(f *o.DeliveryLedgerFacts) { f.Rows[1] = proto.Clone(f.Rows[0]).(*o.DeliveryRow) },
		"kill without corpse": func(f *o.DeliveryLedgerFacts) { huntFacts(f); f.Kills[0].CorpseId = nil },
		"duplicate kill": func(f *o.DeliveryLedgerFacts) {
			huntFacts(f)
			f.Kills = append(f.Kills, proto.Clone(f.Kills[0]).(*o.KillRecord))
		},
		"future kill":          func(f *o.DeliveryLedgerFacts) { huntFacts(f); f.Kills[0].Tick = proto.Int64(8) },
		"negative potential":   func(f *o.DeliveryLedgerFacts) { huntFacts(f); f.Kills[0].PotentialNutrition = proto.Float64(-1) },
		"butcher without meat": func(f *o.DeliveryLedgerFacts) { huntFacts(f); f.Butchers[0].MeatDef = nil },
		"negative leather":     func(f *o.DeliveryLedgerFacts) { huntFacts(f); f.Butchers[0].LeatherUnits = proto.Int64(-1) },
		"missing meat units":   func(f *o.DeliveryLedgerFacts) { huntFacts(f); f.Butchers[0].MeatUnits = nil },
	} {
		f := good()
		mutate(f)
		unknown[name] = observed(f)
	}
	for name, section := range unknown {
		if _, known := decode(section).Value(); known {
			t.Errorf("%s: a missing or invalid ledger must be unknown, not zero", name)
		}
	}
}

// huntFacts adds one valid kill and its butcher record to a ledger read at tick 7.
func huntFacts(f *o.DeliveryLedgerFacts) {
	f.Kills = []*o.KillRecord{{CorpseId: proto.String("Corpse1"), PawnId: proto.String("Deer1"), Race: proto.String("Deer"), BodySize: proto.Float64(1.2), PotentialNutrition: proto.Float64(1.6), Tick: proto.Int64(4)}}
	f.Butchers = []*o.ButcherRecord{{CorpseId: proto.String("Corpse1"), Recipe: proto.String("ButcherCorpseFlesh"), MeatDef: proto.String("Meat_Deer"), MeatUnits: proto.Int64(60),
		MeatNutrition: proto.Float64(1.2), LeatherUnits: proto.Int64(20), Tick: proto.Int64(6)}}
}
