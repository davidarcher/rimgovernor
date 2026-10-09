package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The title claim gate. A higher title raises the
// holder's expectations, so the colony claims the next one only once the
// holder's bedroom and the next title's throne room already meet their
// requirements, never before. The gate is a pure decision over recorded
// facts; no runtime acts on a TitleClaimNow yet (see TitleClaim).

// TitleClaimReason names why the gate holds, or "" when it claims.
type TitleClaimReason string

const (
	// ClaimUnknown: a fact the gate needs is unread; it holds rather than
	// guess.
	ClaimUnknown TitleClaimReason = "unknown"
	// ClaimNoFavor: no colonist has the favor for the next rung.
	ClaimNoFavor TitleClaimReason = "favor_short"
	// ClaimBedroomUnmet: the holder's solo bedroom falls short of its
	// title's requirements.
	ClaimBedroomUnmet TitleClaimReason = "bedroom_unmet"
	// ClaimThroneUnmet: the next title's throne room is not standing with
	// its throne at the required impressiveness.
	ClaimThroneUnmet TitleClaimReason = "throne_unmet"
)

// TitleClaim is the gate's verdict for one colonist: Claim is set when the
// colonist may claim Title now, otherwise Reason says what holds it.
type TitleClaim struct {
	Holder PawnID
	Title  string
	Claim  bool
	Reason TitleClaimReason
}

// TitleClaimFacts are the recorded facts the gate reads: the royalty read,
// the sleeping census (bedrooms and their pieces), the throne plan with the
// room census and buildings, and each census room's impressiveness.
type TitleClaimFacts struct {
	Royalty        RoyaltyFacts
	Sleeping       SleepingObservation
	BedroomPieces  []FurnitureRoom
	Plan           LayoutPlan
	Rooms          RoomObservation
	Built          []CurrentBuilding
	Impressiveness map[string]float64
}

// nextClaim is the rung a holding would claim next: the first rung above
// the held title (the first rung for none), false at the top of the ladder.
func nextClaim(ladder []RoyalRung, held string) (RoyalRung, bool) {
	next := 0
	if held != "" {
		next = -1
		for i, rung := range ladder {
			if rung.Title == held {
				next = i + 1
			}
		}
		if next < 0 {
			return RoyalRung{}, false
		}
	}
	if next >= len(ladder) {
		return RoyalRung{}, false
	}
	return ladder[next], true
}

// bedroomMet reports whether holder's solo bedroom meets rung's bedroom
// requirements (its impressiveness and furniture); a rung that asks for
// none is met. Unknown when the holder's bedroom furniture is unread.
func bedroomMet(f TitleClaimFacts, holder PawnID, rung RoyalRung) domain.Fact[bool] {
	minImpressiveness, _ := rung.BedroomMinImpressiveness.Value()
	if minImpressiveness <= 0 && len(rung.BedroomThings) == 0 {
		return domain.Known(true)
	}
	furniture := map[string]FurnitureRoom{}
	for _, r := range f.BedroomPieces {
		furniture[r.ID] = r
	}
	for _, s := range soloBedrooms(f.Sleeping) {
		if s.owner != holder {
			continue
		}
		if s.impressiveness < float64(minImpressiveness) {
			return domain.Known(false)
		}
		room, ok := furniture[s.room]
		if !ok && len(rung.BedroomThings) > 0 {
			return domain.Unknown[bool]()
		}
		for _, thing := range rung.BedroomThings {
			if isReplacementBed(thing.AnyOf) {
				continue
			}
			have := 0
			for _, p := range room.Pieces {
				for _, d := range thing.AnyOf {
					if p.Def == string(d) {
						have++
					}
				}
			}
			if have < thing.Count {
				return domain.Known(false)
			}
		}
		return domain.Known(true)
	}
	return domain.Known(false)
}

// throneMet reports whether the throne room of need stands in the plan with
// a throne in it at the title's minimum impressiveness; a rung that asks for
// no throne is met. Unknown when the room's impressiveness is unread.
func throneMet(f TitleClaimFacts, rung RoyalRung) domain.Fact[bool] {
	need, asks := rungRequirement(rung)
	if !asks {
		return domain.Known(true)
	}
	room, ok := f.Plan.ThroneRoomFor(need.MinArea)
	if !ok {
		return domain.Known(false)
	}
	// Census: impressiveness is read by the room's id.
	standing, ok := CensusRoomIn(room, f.Rooms)
	if _, stands := standingThroneIn(room, need, f.Built); !ok || !stands {
		return domain.Known(false)
	}
	if need.MinImpressiveness <= 0 {
		return domain.Known(true)
	}
	have, ok := f.Impressiveness[standing.ID]
	if !ok {
		return domain.Unknown[bool]()
	}
	return domain.Known(have >= float64(need.MinImpressiveness))
}

// NextTitleClaim returns the gate's verdict for the colonist closest to the
// next title: the first holder, by ID, whose favor reaches the next rung's
// FavorNeeded. With no such colonist the verdict is ClaimNoFavor (or
// ClaimUnknown when the ladder or a favor is unread). A claimable colonist
// whose bedroom or the rung's throne room falls short is held, so a title
// is never claimed ahead of the upkeep it brings.
func NextTitleClaim(f TitleClaimFacts) TitleClaim {
	ladder := f.Royalty.Ladder
	if len(ladder) == 0 {
		return TitleClaim{Reason: ClaimUnknown}
	}
	holders := sortedHolders(f.Royalty.Holders)
	verdict := TitleClaim{Reason: ClaimNoFavor}
	for _, id := range holders {
		for _, h := range f.Royalty.Holders[id] {
			rung, ok := nextClaim(ladder, h.Title)
			if !ok {
				continue
			}
			favor, fk := h.Favor.Value()
			needed, nk := rung.FavorNeeded.Value()
			if !fk || !nk {
				if verdict.Reason == ClaimNoFavor {
					verdict = TitleClaim{Holder: id, Title: rung.Title, Reason: ClaimUnknown}
				}
				continue
			}
			if favor < needed {
				continue
			}
			claim := TitleClaim{Holder: id, Title: rung.Title}
			bedroom, bk := bedroomMet(f, id, rung).Value()
			throne, tk := throneMet(f, rung).Value()
			switch {
			case bk && !bedroom:
				claim.Reason = ClaimBedroomUnmet
			case tk && !throne:
				claim.Reason = ClaimThroneUnmet
			case !bk || !tk:
				claim.Reason = ClaimUnknown
			default:
				claim.Claim = true
				return claim
			}
			if verdict.Reason == ClaimNoFavor || verdict.Reason == ClaimUnknown {
				verdict = claim
			}
		}
	}
	return verdict
}

func sortedHolders(m map[PawnID][]RoyalHolding) []PawnID {
	ids := make([]PawnID, 0, len(m))
	for id := range m {
		ids = append(ids, id)
	}
	sort.Slice(ids, func(i, j int) bool { return ids[i] < ids[j] })
	return ids
}
