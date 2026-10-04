package policy

import (
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainPermits spends permit points (#1606, epic #1598). A colonist with
// permit points takes the permit that is worth most to this colony: aid
// first, then trade and drop-pod access, then psycast permits (more so once
// a colonist is a psycaster). The choice is a pure ranking over the royalty
// read; the goal commits it as a choose_permit pawn setting, and the plan
// recorded on the goal (in the save with the goal) is the persisted intent.
const MaintainPermits ConcernID = "MaintainPermits"

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

// PermitIntent is the goal intent a chosen permit records in the save.
type PermitIntent struct {
	Holder  PawnID
	Faction string
	Permit  string
}

// Setting is the write that carries the intent: the colonist chooses the
// permit with the faction.
func (i PermitIntent) Setting() (domain.PawnSettings, error) {
	return domain.NewChoosePermitSetting(domain.PawnID(i.Holder), i.Faction, i.Permit)
}

// Intent is the goal intent for a choice.
func (c PermitChoice) Intent() PermitIntent {
	return PermitIntent{Holder: c.Holder, Faction: c.Faction, Permit: c.Permit}
}

// The worker classes of the permits that act (RoyalTitlePermitDef.workerClass,
// carried by the royalty read): what the permit does for the colony.
const (
	permitWorkerCallAid       = "RoyalTitlePermitWorker_CallAid"
	permitWorkerCallLaborers  = "RoyalTitlePermitWorker_CallLaborers"
	permitWorkerCallShuttle   = "RoyalTitlePermitWorker_CallShuttle"
	permitWorkerDropResources = "RoyalTitlePermitWorker_DropResources"
	permitWorkerOrbitalStrike = "RoyalTitlePermitWorker_OrbitalStrike"
)

// permitCategory classifies a permit by its worker class. A permit with no
// worker of its own (the passive trade permits) or a read that carries none
// falls back to its def name.
func permitCategory(p RoyalPermit) PermitCategory {
	switch p.Worker {
	case permitWorkerCallAid, permitWorkerCallLaborers:
		return PermitAid
	case permitWorkerCallShuttle, permitWorkerDropResources:
		return PermitDropPod
	case permitWorkerOrbitalStrike:
		return PermitOther
	}
	name := p.Name
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
				category := permitCategory(permit)
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

// NextPermitOf is NextPermit of a royalty read that may be unknown.
func NextPermitOf(royalty domain.Fact[RoyaltyFacts]) (PermitChoice, bool) {
	if f, ok := royalty.Value(); ok {
		return NextPermit(f)
	}
	return PermitChoice{}, false
}

// PermitsSpent measures MaintainPermits: true once no worthwhile permit is
// takeable now, unknown without the royalty read (nothing is raised then).
func PermitsSpent(royalty domain.Fact[RoyaltyFacts]) domain.Fact[bool] {
	if _, ok := royalty.Value(); !ok {
		return domain.Unknown[bool]()
	}
	_, owed := NextPermitOf(royalty)
	return domain.Known(!owed)
}
