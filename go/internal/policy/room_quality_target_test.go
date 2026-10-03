package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestRoomQualityTargets(t *testing.T) {
	bed := func(id, room string, owners ...PawnID) SleepingBed {
		return SleepingBed{ID: id, Room: domain.Known(room), Humanlike: domain.Known(true), Owners: owners}
	}
	room := func(id string, imp float64) UpkeepRoom {
		return UpkeepRoom{ID: id, Quality: domain.Known(RoomQuality{Impressiveness: imp})}
	}
	rooms := domain.Known([]UpkeepRoom{room("r1", 10), room("r2", 72), room("r3", 35)})
	beds := []SleepingBed{bed("b1", "r1", "a"), bed("b2", "r2", "b"), bed("b3", "r3", "c", "d")}
	knight := &RoyalTitle{BedroomMinImpressiveness: 60}
	cases := []struct {
		name   string
		tier   BuildTier
		traits map[PawnID]TraitEffects
		people []SleepingPerson
		room   string
		want   RoomTarget
	}{
		{"camp asks nothing", BuildTierCamp, nil, nil, "r1", RoomTarget{Room: "r1", Owners: []PawnID{"a"}}},
		{"industrial baseline", BuildTierIndustrial, nil, nil, "r1", RoomTarget{Room: "r1", Owners: []PawnID{"a"}, Min: 40, Reasons: []string{"tier"}}},
		{"greedy", BuildTierMasonry, map[PawnID]TraitEffects{"a": {Greedy: true}}, nil, "r1", RoomTarget{Room: "r1", Owners: []PawnID{"a"}, Min: 50, Reasons: []string{"greedy", "tier"}}},
		{"jealous matches best other room", BuildTierCamp, map[PawnID]TraitEffects{"a": {Jealous: true}}, nil, "r1", RoomTarget{Room: "r1", Owners: []PawnID{"a"}, Min: 72, Reasons: []string{"jealous"}}},
		{"jealous in the best room", BuildTierCamp, map[PawnID]TraitEffects{"b": {Jealous: true}}, nil, "r2", RoomTarget{Room: "r2", Owners: []PawnID{"b"}, Min: 35, Reasons: []string{"jealous"}}},
		{"ascetic never upgrades", BuildTierSpacer, map[PawnID]TraitEffects{"a": {Ascetic: true}}, nil, "r1", RoomTarget{Room: "r1", Owners: []PawnID{"a"}, Max: 40, NeverUpgrade: true, Reasons: []string{"ascetic"}}},
		{"royal title", BuildTierPowered, nil, []SleepingPerson{{ID: "a", Title: knight}}, "r1", RoomTarget{Room: "r1", Owners: []PawnID{"a"}, Min: 60, Reasons: []string{"tier", "title"}}},
		{"couple takes the larger floor", BuildTierMasonry, map[PawnID]TraitEffects{"d": {Greedy: true}}, nil, "r3", RoomTarget{Room: "r3", Owners: []PawnID{"c", "d"}, Min: 50, Reasons: []string{"greedy", "tier"}}},
		{"greedy beats ascetic partner", BuildTierCamp, map[PawnID]TraitEffects{"c": {Ascetic: true}, "d": {Greedy: true}}, nil, "r3", RoomTarget{Room: "r3", Owners: []PawnID{"c", "d"}, Min: 50, Reasons: []string{"greedy"}}},
		{"ascetic partner of plain pawn takes baseline", BuildTierPowered, map[PawnID]TraitEffects{"c": {Ascetic: true}}, nil, "r3", RoomTarget{Room: "r3", Owners: []PawnID{"c", "d"}, Min: 30, Reasons: []string{"tier"}}},
		{"ascetic couple", BuildTierSpacer, map[PawnID]TraitEffects{"c": {Ascetic: true}, "d": {Ascetic: true}}, nil, "r3", RoomTarget{Room: "r3", Owners: []PawnID{"c", "d"}, Max: 40, NeverUpgrade: true, Reasons: []string{"ascetic"}}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			got := RoomQualityTargets(SleepingObservation{People: c.people, Beds: beds, Rooms: rooms}, c.traits, c.tier, testImpressiveness)
			if len(got) != 3 {
				t.Fatalf("targets = %d, want 3", len(got))
			}
			if !reflect.DeepEqual(got[c.room], c.want) {
				t.Fatalf("got %+v, want %+v", got[c.room], c.want)
			}
		})
	}
	if got := RoomQualityTargets(SleepingObservation{Beds: beds}, nil, BuildTierCamp, testImpressiveness); got != nil {
		t.Fatalf("unknown census = %v, want nil", got)
	}
	medical := bed("m", "r1", "a")
	medical.Medical = domain.Known(true)
	if got := RoomQualityTargets(SleepingObservation{Beds: []SleepingBed{medical}, Rooms: rooms}, nil, BuildTierCamp, testImpressiveness); len(got) != 0 {
		t.Fatalf("medical bed = %v, want none", got)
	}
}
