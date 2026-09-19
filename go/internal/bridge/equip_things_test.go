package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

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
