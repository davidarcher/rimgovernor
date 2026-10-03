package policy

import (
	"slices"
	"sort"
)

// ImpressivenessLevels are the minimum scores of the impressiveness stages
// the planners aim at: the scoreStages minScore of the game's Impressiveness
// RoomStatDef, read from the catalog (DefinitionCatalog.ImpressivenessLevels).
// The stages run awful, dull, mediocre, decent, slightly impressive, and on
// up; which stage a tier or trait asks for is the planner's choice.
type ImpressivenessLevels struct {
	Dull, Mediocre, Decent, SlightlyImpressive float64
}

// Baseline is the colony-wide floor by build tier (#610 style), one native
// stage per tier: Camp asks nothing, Masonry dull, Powered mediocre,
// Industrial decent, Spacer slightly impressive.
func (l ImpressivenessLevels) Baseline(tier BuildTier) float64 {
	switch {
	case tier >= BuildTierSpacer:
		return l.SlightlyImpressive
	case tier >= BuildTierIndustrial:
		return l.Decent
	case tier >= BuildTierPowered:
		return l.Mediocre
	case tier >= BuildTierMasonry:
		return l.Dull
	}
	return 0
}

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

// CommonRoomTargets returns a RoomTarget per dining and rec room (#816),
// keyed by room id, or nil while the room census is unknown. Every colonist
// eats and relaxes there, so no one's traits apply: the target is the
// colony-wide tier baseline (ImpressivenessLevels.Baseline), reason "common". A room
// holding colonist beds is left to RoomQualityTargets.
func CommonRoomTargets(obs SleepingObservation, tier BuildTier, levels ImpressivenessLevels) map[string]RoomTarget {
	rooms, ok := obs.Rooms.Value()
	if !ok {
		return nil
	}
	min := levels.Baseline(tier)
	targets := map[string]RoomTarget{}
	for _, room := range rooms {
		if len(room.Beds) > 0 || (room.Role != string(RoomRoleDiningRoom) && room.Role != string(RoomRoleRecRoom)) {
			continue
		}
		t := RoomTarget{Room: room.ID, Min: min}
		if min > 0 {
			t.Reasons = []string{"common"}
		}
		targets[room.ID] = t
	}
	return targets
}

// RoomQualityTargets returns a RoomTarget per owned bedroom, keyed by room
// id, or nil while the room census is unknown. A bedroom is a room holding
// a humanlike, non-medical, non-prisoner bed with owners. traits carries each
// pawn's TraitEffects; a missing entry reads as no relevant trait.
//
// Per owner, the floor is the max of:
//   - the tier baseline (ImpressivenessLevels.Baseline), unless the owner is ascetic;
//   - Greedy: slightly impressive, the first stage ThoughtWorker_Greedy
//     leaves null in Core ThoughtDefs/Thoughts_Situation_Traits.xml;
//   - Jealous: the highest impressiveness among the colony's other owned
//     bedrooms (ThoughtWorker_BedroomJealous fires on any better one);
//   - a royal title's BedroomMinImpressiveness.
//
// An Ascetic owner caps the room below decent: ThoughtWorker_Ascetic's
// mood bonus covers only awful, dull and mediocre.
//
// Several owners (a couple) combine as: Min is the largest owner floor; the
// ascetic ceiling applies only when every owner is ascetic and Min stays
// below it. A shared room cannot please both a greedy and an ascetic
// partner, and the demanded floor wins because a missed demand costs more
// mood (-4 to -8, or a title's) than the lost ascetic bonus (+3 to +5).
// NeverUpgrade holds only when the ceiling holds and Min is zero.
func RoomQualityTargets(obs SleepingObservation, traits map[PawnID]TraitEffects, tier BuildTier, levels ImpressivenessLevels) map[string]RoomTarget {
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
			// A trait or title names itself only when it asks above the
			// tier baseline, so a suite claim can tell a raised target
			// from a tier-only one (#1221).
			if v > 0 && (why == "tier" || v > levels.Baseline(tier)) {
				reasons[why] = true
			}
		}
		allAscetic := true
		for _, pawn := range pawns {
			e := traits[pawn]
			if !e.Ascetic {
				allAscetic = false
				raise(levels.Baseline(tier), "tier")
			}
			if e.Greedy {
				raise(levels.SlightlyImpressive, "greedy")
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
		if allAscetic && t.Min < levels.Decent {
			t.Max = levels.Decent
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
