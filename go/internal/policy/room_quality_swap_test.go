package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func swapFixture() SleepingObservation {
	all := []PawnID{"a", "b", "c", "d", "e"}
	bed := func(id, room string, owners ...PawnID) SleepingBed {
		return SleepingBed{ID: id, Room: domain.Known(room), Humanlike: domain.Known(true), Owners: owners, AccessibleTo: all}
	}
	room := func(id string, imp float64) UpkeepRoom {
		return UpkeepRoom{ID: id, Quality: domain.Known(RoomQuality{Impressiveness: imp})}
	}
	return SleepingObservation{
		Beds:  []SleepingBed{bed("b1", "r1", "a"), bed("b2", "r2", "b"), bed("b3", "r3", "c"), bed("b4", "r4", "d", "e")},
		Rooms: domain.Known([]UpkeepRoom{room("r1", 10), room("r2", 72), room("r3", 35), room("r4", 90)}),
	}
}

func TestNextBedroomSwap(t *testing.T) {
	cases := []struct {
		name   string
		traits map[PawnID]TraitEffects
		want   BedroomSwap
		ok     bool
	}{
		{"no traits", nil, BedroomSwap{}, false},
		{"jealous takes best solo room", map[PawnID]TraitEffects{"a": {Jealous: true}}, BedroomSwap{"a", "b2", "b1"}, true},
		{"jealous already best", map[PawnID]TraitEffects{"b": {Jealous: true}}, BedroomSwap{}, false},
		{"jealous never displaces jealous", map[PawnID]TraitEffects{"a": {Jealous: true}, "b": {Jealous: true}}, BedroomSwap{"a", "b3", "b1"}, true},
		{"ascetic takes plainest", map[PawnID]TraitEffects{"b": {Ascetic: true}}, BedroomSwap{"b", "b1", "b2"}, true},
		{"ascetic already plainest", map[PawnID]TraitEffects{"a": {Ascetic: true}}, BedroomSwap{}, false},
		{"ascetic never takes a jealous room", map[PawnID]TraitEffects{"b": {Ascetic: true}, "a": {Jealous: true}}, BedroomSwap{"a", "b2", "b1"}, true},
		{"couple room never swapped", map[PawnID]TraitEffects{"d": {Ascetic: true}}, BedroomSwap{}, false},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			got, ok := NextBedroomSwap(swapFixture(), c.traits)
			if ok != c.ok || got != c.want {
				t.Fatalf("got %+v %v, want %+v %v", got, ok, c.want, c.ok)
			}
		})
	}
	obs := swapFixture()
	obs.Beds[1].AccessibleTo = []PawnID{"b"}
	if got, ok := NextBedroomSwap(obs, map[PawnID]TraitEffects{"a": {Jealous: true}}); !ok || got.Bed != "b3" {
		t.Fatalf("inaccessible best room: got %+v %v", got, ok)
	}
}

func TestSleepingAssignNeverUpgradesAscetic(t *testing.T) {
	obs := swapFixture()
	// b1 (r1, 10) is a's bed but unsuitable now; vacant beds v2 (r2, 72)
	// and v3 (r3, 35) remain.
	obs.Beds = append(obs.Beds,
		SleepingBed{ID: "v2", Room: domain.Known("r2"), Humanlike: domain.Known(true)},
		SleepingBed{ID: "v3", Room: domain.Known("r3"), Humanlike: domain.Known(true)})
	traits := map[PawnID]TraitEffects{"a": {Ascetic: true}}
	request := SleepingRequest{
		Targets:     domain.Known([]SleepingTarget{{Pawn: "a", Kind: SleepingUpgrade, PreviousBed: "b1", Available: []string{"v2", "v3"}}}),
		Sleeping:    domain.Known(obs),
		Traits:      traits,
		RoomTargets: RoomQualityTargets(obs, traits, BuildTierSpacer),
	}
	choice, err := SelectSleepingMethod(request)
	if err != nil || choice.Method == SleepingAssign {
		t.Fatalf("ascetic upgraded: %+v %v", choice, err)
	}
	// Without a current bed the ascetic takes the plainest vacant one.
	request.Targets = domain.Known([]SleepingTarget{{Pawn: "a", Kind: SleepingUpgrade, Available: []string{"v2", "v3"}}})
	if choice, err = SelectSleepingMethod(request); err != nil || choice.Bed != "v3" {
		t.Fatalf("ascetic without bed: %+v %v", choice, err)
	}
}
