package policy

import (
	"sort"
	"strings"
)

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

// Among restricts profiles to the pawns native already found eligible for a
// role (a trade census's negotiators, a squad's defenders), so a role picks
// only among them. An empty id list admits nobody.
func Among(profiles []PawnProfile, ids []PawnID) []PawnProfile {
	allowed := make(map[PawnID]bool, len(ids))
	for _, id := range ids {
		allowed[id] = true
	}
	var out []PawnProfile
	for _, p := range profiles {
		if allowed[p.ID] {
			out = append(out, p)
		}
	}
	return out
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
		score := float64(p.SkillFor(WorkWarden).Level) + 5*float64(p.Effects.Sociable)
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
		s := p.SkillFor(WorkHandling)
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
		candidates = append(candidates, roleCandidate{p.ID, float64(p.SkillFor(WorkHunting).Level) + 5*p.Effects.MoveSpeed})
	}
	return bestRole(candidates)
}

// HuntsPerHunter is the prey one hunter keeps designated at once (#2170).
const HuntsPerHunter = 3

// MaxHuntRows bounds the hunt rows of one acquisition method, whatever the
// roster.
const MaxHuntRows = 48

// HuntBudget is the hunts a goal may still designate: HuntsPerHunter for each
// ranged Hunting-capable colonist (the HunterFor profiles), less the hunts
// already designated, at most MaxHuntRows.
func HuntBudget(profiles []PawnProfile, pendingHunts int) int {
	return max(0, min(MaxHuntRows, Hunters(profiles)*HuntsPerHunter)-pendingHunts)
}

// Hunters counts the ranged Hunting-capable colonists (HunterFor's profiles).
func Hunters(profiles []PawnProfile) int {
	n := 0
	for _, p := range profiles {
		if p.Ranged && p.Capable(WorkHunting, 0) {
			n++
		}
	}
	return n
}

// UpgradeRoleWeight scales a body part's surgery value by the pawn's role
// (#1167): shooters' eyes by Shooting, workers' arms and hands by their
// best manual skill (Construction, Mining, Crafting), haulers' legs and
// feet by half again. Any other part, or a pawn outside the role, is 1.
func UpgradeRoleWeight(p PawnProfile, part string) float64 {
	switch {
	case strings.Contains(part, "Eye"):
		if s := p.Skill("Shooting"); p.Ranged && !s.Disabled {
			return 1 + float64(s.Level)/10
		}
	case strings.Contains(part, "Arm"), strings.Contains(part, "Hand"), strings.Contains(part, "Shoulder"):
		best := -1
		for _, name := range []string{"Construction", "Mining", "Crafting"} {
			if s := p.Skill(name); !s.Disabled && s.Level > best {
				best = s.Level
			}
		}
		if best >= 0 {
			return 1 + float64(best)/10
		}
	case strings.Contains(part, "Leg"), strings.Contains(part, "Foot"):
		if p.Capable(WorkHauling, 0) {
			return 1.5
		}
	}
	return 1
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
