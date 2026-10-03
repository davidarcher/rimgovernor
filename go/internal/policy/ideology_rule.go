package policy

import (
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The shared precept rule (#1655, epic #1653). One pure function answers:
// given the ideoligion, does a precept in force allow, approve, penalise or
// forbid an action, for one pawn or for the colony, and what mood does it
// cost? Callers name the action by the game's HistoryEventDef (the event the
// action raises, which precept effects react to), so no def name is listed
// here and a modded precept answers like a vanilla one. Unknown stays
// unknown: the rule never guesses a stance it cannot read.

// PreceptStance is the ideoligion's answer to an action.
type PreceptStance string

const (
	// PreceptUnknown holds the action: the ideoligion is unread, or an effect
	// bears on the action in a way the contract does not type.
	PreceptUnknown PreceptStance = "unknown"
	// PreceptAllowed: no precept in force reacts to the action.
	PreceptAllowed PreceptStance = "allowed"
	// PreceptApproved: precepts only give mood for the action.
	PreceptApproved PreceptStance = "approved"
	// PreceptPenalised: the action costs mood, or members may refuse it
	// (RefusalChance below 1).
	PreceptPenalised PreceptStance = "penalised"
	// PreceptForbidden: members refuse the action outright.
	PreceptForbidden PreceptStance = "forbidden"
)

// PreceptAction is the action asked about: the HistoryEventDef it raises, or
// Apparel for what a pawn wears (apparel precepts carry no history event).
type PreceptAction struct {
	HistoryEvent string
	Apparel      bool
}

// PreceptSubject is who acts. The zero value is the colony: no pawn's traits
// are known, so no refusal is cancelled. For a pawn, its traits and hediffs
// cancel an unwilling effect that lists them, and a slave skips effects that
// apply only to non-slaves.
type PreceptSubject struct {
	Pawn    bool
	Slave   bool
	Traits  []string
	Hediffs []string
}

// PreceptVerdict is the rule's answer. DoerMoodCost sums, over the matching
// precept effects the doer takes (self-took-action), the worst stage's mood
// loss; WitnessMoodCost the same for the other penalising effects. Both are
// non-negative. RefusalChance is the largest chance members refuse, 1 when a
// refusal is unconditional. Effects are the matching effects, in precept
// order.
type PreceptVerdict struct {
	Stance          PreceptStance
	DoerMoodCost    float64
	WitnessMoodCost float64
	RefusalChance   float64
	Effects         []EffectInForce
	Reason          string
}

// ActionStance evaluates the action under the ideoligion fact; the verdict
// is PreceptUnknown when the ideoligion is unread.
func ActionStance(ideology domain.Fact[Ideoligion], action PreceptAction, subject PreceptSubject) PreceptVerdict {
	i, ok := ideology.Value()
	if !ok {
		return PreceptVerdict{Stance: PreceptUnknown, Reason: "ideoligion unknown"}
	}
	return i.ActionStance(action, subject)
}

// ActionStance evaluates action by subject under the ideoligion.
func (i Ideoligion) ActionStance(action PreceptAction, subject PreceptSubject) PreceptVerdict {
	if !action.Apparel && action.HistoryEvent == "" {
		return PreceptVerdict{Stance: PreceptUnknown, Reason: "no action named"}
	}
	verdict := PreceptVerdict{Stance: PreceptAllowed}
	approved := false
	for _, in := range i.EffectsInForce() {
		e := in.Effect
		if action.Apparel {
			if e.Kind == EffectApparel {
				// The apparel effect carries no requirement on the wire.
				return PreceptVerdict{Stance: PreceptUnknown, Effects: []EffectInForce{in}, Reason: "apparel effect of " + in.Precept.Name + " is not typed"}
			}
			continue
		}
		if e.HistoryEvent != action.HistoryEvent || e.Kind == EffectApparel || (e.OnlyForNonSlaves && subject.Slave) {
			continue
		}
		switch {
		case e.Forbids():
			if subject.Pawn && cancels(e, subject) {
				continue
			}
			verdict.Effects = append(verdict.Effects, in)
			chance := 1.0
			if c, ok := e.Chance.Value(); ok {
				chance = math.Min(c, 1)
			}
			verdict.RefusalChance = math.Max(verdict.RefusalChance, chance)
		case e.Penalises():
			verdict.Effects = append(verdict.Effects, in)
			cost := -slices.Min(e.StageMoods)
			if e.Kind == EffectSelfTookAction {
				verdict.DoerMoodCost += cost
			} else {
				verdict.WitnessMoodCost += cost
			}
		case e.Approves():
			verdict.Effects = append(verdict.Effects, in)
			approved = true
		}
	}
	switch {
	case verdict.RefusalChance >= 1:
		verdict.Stance = PreceptForbidden
	case verdict.RefusalChance > 0 || verdict.DoerMoodCost > 0 || verdict.WitnessMoodCost > 0:
		verdict.Stance = PreceptPenalised
	case approved:
		verdict.Stance = PreceptApproved
	}
	return verdict
}

func cancels(e PreceptEffect, s PreceptSubject) bool {
	for _, t := range e.NullifyingTraits {
		if slices.Contains(s.Traits, t) {
			return true
		}
	}
	for _, h := range e.NullifyingHediffs {
		if slices.Contains(s.Hediffs, h) {
			return true
		}
	}
	return false
}
