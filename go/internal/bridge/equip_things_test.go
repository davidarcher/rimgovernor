package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestEquipCensusBiocodeIdentity(t *testing.T) {
	fixture := func() *o.ListSuppliesReply {
		reply := equipTestRead()
		stock := reply.GetObserved().Stocks[0]
		second := proto.Clone(stock.Items[0]).(*o.EntityRef)
		second.Id = proto.String("other-sling")
		second.Snapshot.EntityId = second.Id
		stock.Items = append(stock.Items, second)
		stock.Units = proto.Int64(2)
		stock.ItemsCompleteness = emergencyCounts(2)
		// Reverse order: ownership must join by ID, not definition or index.
		stock.WeaponItems = []*o.GearItem{
			{Thing: proto.Clone(second).(*o.EntityRef), Biocoded: proto.Bool(false)},
			{Thing: proto.Clone(stock.Items[0]).(*o.EntityRef), Biocoded: proto.Bool(true), BiocodedTo: proto.String("pawn-b")},
		}
		return reply
	}
	read, err := decodeEquipWeapons(fixture(), pbIdentity(), domain.Cell{}, domain.Cell{X: 10, Z: 10})
	if err != nil {
		t.Fatal(err)
	}
	if len(read.Targets) != 4 || read.Targets[0].BiocodedTo != "pawn-b" || !read.Targets[0].Biocoded || read.Targets[1].BiocodedTo != "" || read.Targets[1].Biocoded {
		t.Fatal(read)
	}
	for name, edit := range map[string]func(*o.ResourceStock){
		"empty owner":    func(s *o.ResourceStock) { s.WeaponItems[1].BiocodedTo = proto.String("") },
		"contradictory":  func(s *o.ResourceStock) { s.WeaponItems[1].Biocoded = proto.Bool(false) },
		"duplicate":      func(s *o.ResourceStock) { s.WeaponItems[0] = s.WeaponItems[1] },
		"partial":        func(s *o.ResourceStock) { s.WeaponItems = s.WeaponItems[:1] },
		"other identity": func(s *o.ResourceStock) { s.WeaponItems[1].Thing.Id = proto.String("unlisted") },
		"other map":      func(s *o.ResourceStock) { s.WeaponItems[1].Thing.MapId = proto.Int32(999) },
		"other position": func(s *o.ResourceStock) { s.WeaponItems[1].Thing.Position.X = proto.Int32(9) },
	} {
		t.Run(name, func(t *testing.T) {
			reply := fixture()
			edit(reply.GetObserved().Stocks[0])
			if _, err := decodeEquipWeapons(reply, pbIdentity(), domain.Cell{}, domain.Cell{X: 10, Z: 10}); err == nil {
				t.Fatal("invalid ownership accepted")
			}
		})
	}
}

func equipTestRead() *o.ListSuppliesReply {
	ctx := buildingAdmission().AdmittedContext
	stock := func(def, id string, byTrade, ranged, melee bool) *o.ResourceStock {
		item := &o.EntityRef{Id: proto.String(id), DefName: proto.String(def), MapId: pbIdentity().MapId, Position: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}, Snapshot: &o.SnapshotRef{Context: proto.Clone(ctx).(*c.ObservationContext), EntityId: proto.String(id), Token: proto.String("snapshot-" + id)}}
		return &o.ResourceStock{Definition: &o.DefinitionRef{DefName: proto.String(def)}, Units: proto.Int64(1), Items: []*o.EntityRef{item}, ItemsCompleteness: emergencyCounts(1), WeaponByTrade: proto.Bool(byTrade), Ranged: proto.Bool(ranged), Melee: proto.Bool(melee)}
	}
	return &o.ListSuppliesReply{Outcome: &o.ListSuppliesReply_Observed{Observed: &o.SuppliesSnapshot{Context: ctx, Completeness: emergencyCounts(3), Stocks: []*o.ResourceStock{stock("Modded_Sling", "sling", true, true, false), stock("MeleeWeapon_Club", "club", true, false, true), stock("WoodLog", "log", false, false, true)}}}}
}

// The weapon class is the census's own category membership and
// IsRangedWeapon/IsMeleeWeapon, not the def name (#287).
func TestEquipCensusCarriesObservedWeaponClass(t *testing.T) {
	read, err := decodeEquipWeapons(equipTestRead(), pbIdentity(), domain.Cell{}, domain.Cell{X: 10, Z: 10})
	if err != nil || len(read.Targets) != 3 {
		t.Fatal(read, err)
	}
	want := map[string][3]bool{"sling": {true, true, false}, "club": {true, false, true}, "log": {false, false, true}}
	for _, target := range read.Targets {
		if got := [3]bool{target.ByTrade, target.Ranged, target.Melee}; got != want[target.Thing] {
			t.Fatal(target)
		}
	}
	for name, edit := range map[string]func(*o.ResourceStock){
		"trade-missing":  func(s *o.ResourceStock) { s.WeaponByTrade = nil },
		"ranged-missing": func(s *o.ResourceStock) { s.Ranged = nil },
		"melee-missing":  func(s *o.ResourceStock) { s.Melee = nil },
		"both":           func(s *o.ResourceStock) { s.Ranged, s.Melee = proto.Bool(true), proto.Bool(true) },
	} {
		reply := equipTestRead()
		edit(reply.GetObserved().Stocks[0])
		if _, err := decodeEquipWeapons(reply, pbIdentity(), domain.Cell{}, domain.Cell{X: 10, Z: 10}); err == nil {
			t.Fatal(name)
		}
	}
}
