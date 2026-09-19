package policy

import "sort"

// Situational roles: pure answers to the questions other goals ask of the
// roster (who operates, who wardens, who trades, who tames, who holds the
// line), all from the same PawnProfile the planner scores. Each returns the
// chosen pawn in a deterministic order (fitness, then ID) and false when no
// pawn qualifies.

type roleCandidate struct {
	id    PawnID
	score float64
}

func bestRole(candidates []roleCandidate) (PawnID, bool) {
	if len(candidates) == 0 {
		return "", false
	}
	sort.Slice(candidates, func(i, j int) bool {
		if candidates[i].score != candidates[j].score {
			return candidates[i].score > candidates[j].score
		}
		return candidates[i].id < candidates[j].id
	})
	return candidates[0].id, true
}

// SurgeonFor picks the surgeon for an operation needing Medicine at least
// minimum (never under 4): the highest Medicine, a Psychopath preferred for
// a harvest since the operation costs them no mood.
func SurgeonFor(profiles []PawnProfile, minimum int, harvest bool) (PawnID, bool) {
	if minimum < 4 {
		minimum = 4
	}
	var candidates []roleCandidate
	for _, p := range profiles {
		s := p.Skill("Medicine")
		if !p.Capable(WorkDoctor, minimum) || s.Level < minimum {
			continue
		}
		score := float64(s.Level)
		if harvest && p.Effects.SurgeonSafe {
			score += 5
		}
		candidates = append(candidates, roleCandidate{p.ID, score})
	}
	return bestRole(candidates)
}

// WardenFor picks the warden: the highest Social, Kind preferred, Abrasive
// never; for an execution a Psychopath or Bloodlust pawn takes no mood loss
// and is preferred.
func WardenFor(profiles []PawnProfile, execution bool) (PawnID, bool) {
	var candidates []roleCandidate
	for _, p := range profiles {
		if !p.Capable(WorkWarden, 0) {
			continue
		}
		score := float64(p.Skill("Social").Level) + 5*float64(p.Effects.Sociable)
		if execution && p.Effects.Execution {
			score += 10
		}
		candidates = append(candidates, roleCandidate{p.ID, score})
	}
	return bestRole(candidates)
}

// TraderFor picks the negotiator: the highest Social, never Abrasive while
// anyone else can talk.
func TraderFor(profiles []PawnProfile) (PawnID, bool) {
	var candidates, abrasive []roleCandidate
	for _, p := range profiles {
		s := p.Skill("Social")
		if s.Disabled || p.Incapable[WorkWarden] {
			continue
		}
		c := roleCandidate{p.ID, float64(s.Level) + 5*float64(p.Effects.Sociable)}
		if p.Effects.Sociable < 0 {
			abrasive = append(abrasive, c)
			continue
		}
		candidates = append(candidates, c)
	}
	if len(candidates) == 0 {
		candidates = abrasive
	}
	return bestRole(candidates)
}

// TamerFor picks the handler for an animal needing Animals at least minimum
// (the animal read's minimum_handling_skill).
func TamerFor(profiles []PawnProfile, minimum int) (PawnID, bool) {
	var candidates []roleCandidate
	for _, p := range profiles {
		s := p.Skill("Animals")
		if !p.Capable(WorkHandling, minimum) {
			continue
		}
		candidates = append(candidates, roleCandidate{p.ID, float64(s.Level)})
	}
	return bestRole(candidates)
}

// HunterFor picks the hunter: Shooting with a ranged weapon, never a
// Brawler, a fast walker preferred for the walk to the carcass.
func HunterFor(profiles []PawnProfile) (PawnID, bool) {
	var candidates []roleCandidate
	for _, p := range profiles {
		if !p.Ranged || !p.Capable(WorkHunting, 0) {
			continue
		}
		candidates = append(candidates, roleCandidate{p.ID, float64(p.Skill("Shooting").Level) + 5*p.Effects.MoveSpeed})
	}
	return bestRole(candidates)
}

// FrontLine and RearRanged split a defending roster: melee pawns (Tough,
// Nimble, Brawler, or Melee over Shooting) hold the line and the rest shoot
// from behind it. Every non-violent-incapable pawn lands in exactly one.
func FrontLine(profiles []PawnProfile) (front, rear []PawnID) {
	for _, p := range profiles {
		melee, shooting := p.Skill("Melee"), p.Skill("Shooting")
		if melee.Disabled && shooting.Disabled {
			continue
		}
		if p.Effects.FrontLine || !p.Effects.RearRanged && !p.Ranged && melee.Level >= shooting.Level {
			front = append(front, p.ID)
			continue
		}
		rear = append(rear, p.ID)
	}
	return front, rear
}
