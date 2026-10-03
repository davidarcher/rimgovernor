package policy

import (
	"sort"
	"strings"
)

// The permit selection goal (#1606, epic #1598). A colonist with permit
// points spends them on the permit that is worth most to this colony:
// aid first, then trade and drop-pod access, then psycast permits (more
// so once a colonist is a psycaster). The choice is a pure ranking over the
// royalty read.
//
// Seam: choosing a permit is a write (Pawn_RoyaltyTracker.AddPermit, the
// player's title UI) that no action kind carries, and recording the choice
// as goal intent in the save needs a goal kind. Until a decision adds an
// action kind (#1606), nothing acts on a PermitChoice; PermitIntent is the
// shape the goal would persist.

// PermitCategory groups permits by what they give the colony.
type PermitCategory string

const (
	PermitAid     PermitCategory = "aid"
	PermitTrade   PermitCategory = "trade"
	PermitDropPod PermitCategory = "drop_pod"
	PermitPsycast PermitCategory = "psycast"
	PermitOther   PermitCategory = "other"
)

// PermitReason names why a candidate cannot be taken now, or "" when it can.
type PermitReason string

const (
	// PermitUnknownCost: the permit's point cost or the holder's points or
	// title are unread.
	PermitUnknownCost PermitReason = "unknown"
	// PermitTitleShort: the holder's title is below the permit's minimum.
	PermitTitleShort PermitReason = "title_short"
	// PermitPointsShort: the holder lacks the permit points.
	PermitPointsShort PermitReason = "points_short"
)

// PermitChoice is one candidate permit for one colonist's holding. Value is
// the colony's ranking score and Cost the permit points it takes; Take is
// set when the permit is eligible and affordable now, otherwise Reason says
// why not.
type PermitChoice struct {
	Holder   PawnID
	Faction  string
	Permit   string
	Category PermitCategory
	Value    int
	Cost     int
	Take     bool
	Reason   PermitReason
}

// PermitIntent is the goal intent a chosen permit would record in the save.
type PermitIntent struct {
	Holder  PawnID
	Faction string
	Permit  string
}

// Intent is the goal intent for a choice.
func (c PermitChoice) Intent() PermitIntent {
	return PermitIntent{Holder: c.Holder, Faction: c.Faction, Permit: c.Permit}
}

// permitCategory classifies a permit def by name; the catalog carries no
// effect, only whether the permit acts.
func permitCategory(name string) PermitCategory {
	switch {
	case strings.Contains(name, "Psy"):
		return PermitPsycast
	case strings.Contains(name, "Aid"), strings.Contains(name, "Laborer"):
		return PermitAid
	case strings.Contains(name, "Trade"):
		return PermitTrade
	case strings.Contains(name, "Drop"), strings.Contains(name, "Shuttle"), strings.Contains(name, "Transport"):
		return PermitDropPod
	}
	return PermitOther
}

// permitValue scores a category for this colony: psycast permits outrank
// trade once any colonist is a psycaster.
func permitValue(category PermitCategory, psycaster bool) int {
	switch category {
	case PermitAid:
		return 40
	case PermitTrade:
		return 30
	case PermitDropPod:
		return 20
	case PermitPsycast:
		if psycaster {
			return 35
		}
		return 10
	}
	return 0
}

func titleSeniority(ladder []RoyalRung, title string) (int, bool) {
	for _, rung := range ladder {
		if rung.Title == title {
			return rung.Seniority.Value()
		}
	}
	return 0, false
}

// RankPermits lists every untaken permit for every titled colonist holding,
// best first: takeable before blocked, then by value, then cheaper, then by
// holder and name. A holding without a title cannot take permits.
func RankPermits(f RoyaltyFacts) []PermitChoice {
	psycaster := false
	for _, casts := range f.Psycasts {
		if len(casts) > 0 {
			psycaster = true
		}
	}
	names := make([]string, 0, len(f.Permits))
	for name := range f.Permits {
		names = append(names, name)
	}
	sort.Strings(names)
	var out []PermitChoice
	for _, holder := range sortedHolders(f.Holders) {
		for _, h := range f.Holders[holder] {
			if h.Title == "" {
				continue
			}
			held := map[string]bool{}
			for _, p := range h.Permits {
				held[p] = true
			}
			rank, rankKnown := titleSeniority(f.Ladder, h.Title)
			points, pointsKnown := h.PermitPoints.Value()
			for _, name := range names {
				if held[name] {
					continue
				}
				permit := f.Permits[name]
				category := permitCategory(name)
				c := PermitChoice{Holder: holder, Faction: h.FactionDef, Permit: name,
					Category: category, Value: permitValue(category, psycaster)}
				cost, costKnown := permit.PermitPoints.Value()
				c.Cost = cost
				need, needKnown := 0, true
				if min, ok := permit.MinTitle.Value(); ok && min != "" {
					need, needKnown = titleSeniority(f.Ladder, min)
				}
				switch {
				case !costKnown || !pointsKnown || !rankKnown || !needKnown:
					c.Reason = PermitUnknownCost
				case rank < need:
					c.Reason = PermitTitleShort
				case points < cost:
					c.Reason = PermitPointsShort
				default:
					c.Take = true
				}
				out = append(out, c)
			}
		}
	}
	sort.SliceStable(out, func(i, j int) bool {
		a, b := out[i], out[j]
		switch {
		case a.Take != b.Take:
			return a.Take
		case a.Value != b.Value:
			return a.Value > b.Value
		case a.Cost != b.Cost:
			return a.Cost < b.Cost
		case a.Holder != b.Holder:
			return a.Holder < b.Holder
		}
		return a.Permit < b.Permit
	})
	return out
}

// NextPermit is the best permit to take now, false when none is takeable or
// the best takeable one is worth nothing to the colony.
func NextPermit(f RoyaltyFacts) (PermitChoice, bool) {
	ranked := RankPermits(f)
	if len(ranked) == 0 || !ranked[0].Take || ranked[0].Value <= 0 {
		return PermitChoice{}, false
	}
	return ranked[0], true
}
