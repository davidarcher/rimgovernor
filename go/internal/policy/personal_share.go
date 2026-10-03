package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// PersonalShareFraction is f, the part of the personal pool colonists may
// direct at themselves (#1829). It must stay below 1: personal spend adds to
// the colony's wealth and so to the pool, and with f < 1 the total converges
// at f/(1-f) x the starting pool (0.2 gives a quarter), where f >= 1 would
// let spending chase its own growth without bound.
const PersonalShareFraction = 0.2

// Share weight modifiers over the equal base of 1 (#1836). A role adds to the
// claim its owner presses; an Ascetic claims half the base and nothing more.
const (
	shareWeightSoldier = 0.25 // gear
	shareWeightDoctor  = 0.25 // bionics
	shareWeightGreedy  = 0.5  // bedroom
	shareWeightJealous = 0.25 // bedroom
	shareWeightAscetic = 0.5
)

// PersonalPool is the wealth a colony's personal shares divide: items plus
// buildings, pawns excluded so animals, prisoners and installed bionics do
// not inflate it (#1836). Unknown unless the wealth fact is known and both
// parts are finite and non-negative, like WealthBudget.
func PersonalPool(wealth domain.Fact[WealthFacts]) domain.Fact[float64] {
	w, ok := wealth.Value()
	if !ok || !finite(w.Items) || !finite(w.Buildings) || w.Items < 0 || w.Buildings < 0 {
		return domain.Unknown[float64]()
	}
	return domain.Known(w.Items + w.Buildings)
}

// ElectiveShare is the elective surgery gate over f's held shares: a colonist
// with no entry gets UnknownPersonalShare (necessities only); nil shares are
// ungated.
func (f RoutineFacts) ElectiveShare() ElectiveShare {
	if f.PersonalShares == nil {
		return ElectiveShare{}
	}
	return ElectiveShare{Items: f.Items, Of: func(pawn PawnID) PersonalShare {
		if s, ok := f.PersonalShares[pawn]; ok {
			return s
		}
		return UnknownPersonalShare()
	}}
}

// PersonalPool is the pool from f's wealth.
func (f RoutineFacts) PersonalPool() domain.Fact[float64] { return PersonalPool(f.Wealth) }

// ShareMember is one colonist's input to PersonalShares. Soldier and Doctor
// come from the caller's roster (SoldierSquad, the surgeon role); Spent is the
// derived attribution of what the colonist holds (#1838, #1839), unknown until
// read.
type ShareMember struct {
	Profile PawnProfile
	// Free is a free colonist; slaves and prisoners are not, and get
	// necessities only.
	Free            bool
	Soldier, Doctor bool
	Spent           domain.Fact[float64]
}

// ShareWeight is the colonist's claim on the pool: an equal base of 1, raised
// by role (soldier, doctor) and by Greedy and Jealous, and an Ascetic at half
// the base.
func ShareWeight(m ShareMember) float64 {
	e := m.Profile.Effects
	if e.Ascetic {
		return shareWeightAscetic
	}
	w := 1.0
	if m.Soldier {
		w += shareWeightSoldier
	}
	if m.Doctor {
		w += shareWeightDoctor
	}
	if e.Greedy {
		w += shareWeightGreedy
	}
	if e.Jealous {
		w += shareWeightJealous
	}
	return w
}

// PersonalShare is what one colonist may direct at themselves: Share is
// f x pool x weight over the free colonists' total weight, Spent the derived
// attribution, and Remaining Share - Spent floored at 0 (an overspent colonist
// has nothing left, never a negative headroom). Share or Spent unknown leaves
// Remaining unknown, which Allows refuses. The zero value is ungated, so
// callers that carry no share keep working.
type PersonalShare struct {
	Share, Spent, Remaining domain.Fact[float64]
	// Gated is exported so a recorded share keeps its gate through JSON.
	Gated bool
}

// Allows is whether the colonist's remaining share covers an upgrade of the
// given market-value delta. A zero-value PersonalShare allows everything, and
// so does a delta of zero or less (necessities are never charged); otherwise
// an unknown remaining refuses.
func (s PersonalShare) Allows(delta float64) bool {
	if !s.Gated || delta <= 0 {
		return true
	}
	remaining, ok := s.Remaining.Value()
	return ok && delta <= remaining
}

// UnknownPersonalShare is the share of a colonist the colony has no share
// for (an unread pool, spent or roster, a slave or a prisoner): gated, with
// every part unknown, so Allows refuses any charged upgrade and only
// necessities pass. Unlike the zero value it is never ungated.
func UnknownPersonalShare() PersonalShare {
	return PersonalShare{Share: domain.Unknown[float64](), Spent: domain.Unknown[float64](), Remaining: domain.Unknown[float64](), Gated: true}
}

// ShareDoctors are the colonists whose claim carries the doctor weight
// (#1846): the DoctorsWanted(len(profiles)) most skilled Medicine doctors
// among those able to doctor (not backstory-incapable, not a child), best
// level first and ties by id.
func ShareDoctors(profiles []PawnProfile) map[PawnID]bool {
	able := []PawnProfile{}
	for _, p := range profiles {
		if p.Capable(WorkDoctor, 0) && !p.Child {
			able = append(able, p)
		}
	}
	sort.SliceStable(able, func(i, j int) bool {
		a, b := able[i].SkillFor(WorkDoctor).Level, able[j].SkillFor(WorkDoctor).Level
		if a != b {
			return a > b
		}
		return able[i].ID < able[j].ID
	})
	out := map[PawnID]bool{}
	for i := 0; i < len(able) && i < DoctorsWanted(len(profiles)); i++ {
		out[able[i].ID] = true
	}
	return out
}

// PersonalShares divides the pool among the free members by weight and pairs
// each member's share with their spent. A member that is not free gets a zero
// share. An unknown pool leaves every free member's share and remaining
// unknown.
func PersonalShares(pool domain.Fact[float64], members []ShareMember) map[PawnID]PersonalShare {
	total := 0.0
	for _, m := range members {
		if m.Free {
			total += ShareWeight(m)
		}
	}
	p, poolKnown := pool.Value()
	poolKnown = poolKnown && finite(p) && p >= 0
	out := make(map[PawnID]PersonalShare, len(members))
	for _, m := range members {
		share := domain.Known(0.0)
		if m.Free {
			share = domain.Unknown[float64]()
			if poolKnown && total > 0 {
				share = domain.Known(PersonalShareFraction * p * ShareWeight(m) / total)
			}
		}
		s := PersonalShare{Share: share, Spent: m.Spent, Gated: true}
		sh, shk := share.Value()
		sp, spk := m.Spent.Value()
		if shk && spk && finite(sp) && sp >= 0 {
			s.Remaining = domain.Known(max(sh-sp, 0))
		}
		out[m.Profile.ID] = s
	}
	return out
}
