package policy

import (
	"slices"
	"sort"
)

// Native impressiveness label thresholds: the scoreStages minScore values of
// RoomStatDef Impressiveness in Core Defs/Rooms/RoomStats.xml (awful below
// 20, dull 20, mediocre 30, decent 40, slightly impressive 50, somewhat 65,
// very 85, extremely 120, unbelievably 170, wondrously 240).
const (
	ImpressivenessDull               = 20.0
	ImpressivenessMediocre           = 30.0
	ImpressivenessDecent             = 40.0
	ImpressivenessSlightlyImpressive = 50.0
)

// RoomTarget is the impressiveness one owned bedroom should reach (#811).
// Consumers (the #813 ranking, the gap closer) compare it against the
// room's observed RoomQuality.Impressiveness.
type RoomTarget struct {
	Room   string
	Owners []PawnID
	// Min is the impressiveness the room should reach; zero asks nothing.
	Min float64
	// Max, when positive, is the exclusive ceiling the room should stay
	// under (ascetic owners); zero means no ceiling.
	Max float64
	// NeverUpgrade marks a room no upgrade should touch: every owner is
	// ascetic and none demands a floor.
	NeverUpgrade bool
	// Reasons name the constraints that set Min or Max, sorted: "tier",
	// "greedy", "jealous", "title", "ascetic".
	Reasons []string
}

// RoomTargetBaseline is the colony-wide floor by build tier (#610 style),
// one native label per tier: Camp asks nothing, Masonry dull, Powered
// mediocre, Industrial decent, Spacer slightly impressive.
func RoomTargetBaseline(tier BuildTier) float64 {
	switch {
	case tier >= BuildTierSpacer:
		return ImpressivenessSlightlyImpressive
	case tier >= BuildTierIndustrial:
		return ImpressivenessDecent
	case tier >= BuildTierPowered:
		return ImpressivenessMediocre
	case tier >= BuildTierMasonry:
		return ImpressivenessDull
	}
	return 0
}

// RoomQualityTargets returns a RoomTarget per owned bedroom, keyed by room
// id, or nil while the room census is unknown. A bedroom is a room holding
// a humanlike, non-medical, non-prisoner bed with owners. traits carries each
// pawn's TraitEffects; a missing entry reads as no relevant trait.
//
// Per owner, the floor is the max of:
//   - the tier baseline (RoomTargetBaseline), unless the owner is ascetic;
//   - Greedy: slightly impressive (50), the first stage ThoughtWorker_Greedy
//     leaves null in Core ThoughtDefs/Thoughts_Situation_Traits.xml;
//   - Jealous: the highest impressiveness among the colony's other owned
//     bedrooms (ThoughtWorker_BedroomJealous fires on any better one);
//   - a royal title's BedroomMinImpressiveness.
//
// An Ascetic owner caps the room below decent (40): ThoughtWorker_Ascetic's
// mood bonus covers only awful, dull and mediocre.
//
// Several owners (a couple) combine as: Min is the largest owner floor; the
// ascetic ceiling applies only when every owner is ascetic and Min stays
// below it. A shared room cannot please both a greedy and an ascetic
// partner, and the demanded floor wins because a missed demand costs more
// mood (-4 to -8, or a title's) than the lost ascetic bonus (+3 to +5).
// NeverUpgrade holds only when the ceiling holds and Min is zero.
func RoomQualityTargets(obs SleepingObservation, traits map[PawnID]TraitEffects, tier BuildTier) map[string]RoomTarget {
	rooms, ok := obs.Rooms.Value()
	if !ok {
		return nil
	}
	impressiveness := map[string]float64{}
	for _, room := range rooms {
		if q, ok := room.Quality.Value(); ok {
			impressiveness[room.ID] = q.Impressiveness
		}
	}
	titles := map[PawnID]*RoyalTitle{}
	for _, p := range obs.People {
		titles[p.ID] = p.Title
	}
	owners := map[string][]PawnID{}
	for _, bed := range obs.Beds {
		room, ok := bed.Room.Value()
		humanlike, _ := bed.Humanlike.Value()
		medical, _ := bed.Medical.Value()
		prisoners, _ := bed.Prisoners.Value()
		if !ok || room == "" || !humanlike || medical || prisoners {
			continue
		}
		for _, o := range bed.Owners {
			if !slices.Contains(owners[room], o) {
				owners[room] = append(owners[room], o)
			}
		}
	}
	targets := make(map[string]RoomTarget, len(owners))
	for room, pawns := range owners {
		slices.Sort(pawns)
		t := RoomTarget{Room: room, Owners: pawns}
		reasons := map[string]bool{}
		raise := func(v float64, why string) {
			if v > t.Min {
				t.Min = v
			}
			if v > 0 {
				reasons[why] = true
			}
		}
		allAscetic := true
		for _, pawn := range pawns {
			e := traits[pawn]
			if !e.Ascetic {
				allAscetic = false
				raise(RoomTargetBaseline(tier), "tier")
			}
			if e.Greedy {
				raise(ImpressivenessSlightlyImpressive, "greedy")
			}
			if e.Jealous {
				best := 0.0
				for other := range owners {
					if v, ok := impressiveness[other]; other != room && ok && v > best {
						best = v
					}
				}
				raise(best, "jealous")
			}
			if title := titles[pawn]; title != nil {
				raise(float64(title.BedroomMinImpressiveness), "title")
			}
		}
		if allAscetic && t.Min < ImpressivenessDecent {
			t.Max = ImpressivenessDecent
			reasons["ascetic"] = true
			t.NeverUpgrade = t.Min == 0
		}
		for why := range reasons {
			t.Reasons = append(t.Reasons, why)
		}
		sort.Strings(t.Reasons)
		targets[room] = t
	}
	return targets
}
