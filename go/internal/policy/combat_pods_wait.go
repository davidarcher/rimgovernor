package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// podStrikeToils are the lord toils of a raid that flees or loots and
// leaves: the pods tactic's wait ends and it strikes (#893).
var podStrikeToils = map[string]bool{
	"LordToil_PanicFlee":                true,
	"LordToil_KidnapCover":              true,
	"LordToil_StealCover":               true,
	"LordToil_ExitMap":                  true,
	"LordToil_ExitMapAndEscortCarriers": true,
}

// podStrength is the strength comparison of the wait option (#893): ours
// is the health fraction summed over the armed, eligible defenders; the
// pod group's is its live hostile count, or the landing cell count while
// none is out yet.
func podStrength(view CombatView, pods PodArrival) (ours, theirs float64) {
	for _, d := range view.Defenders {
		if squadDefenderEligible(d) && positive(d.Armed) {
			h, _ := d.HealthFraction.Value()
			ours += h
		}
	}
	down := map[domain.PawnID]bool{}
	for _, p := range view.Pawns {
		if p.Dead || p.Downed {
			down[p.ID] = true
		}
	}
	for _, t := range view.Positional {
		if !positive(t.Dead) && !positive(t.Downed) && !down[domain.PawnID(t.ID)] {
			theirs++
		}
	}
	if theirs == 0 {
		theirs = float64(len(pods.Landing))
	}
	return ours, theirs
}

// podStrike reports a raid_phase stop with a live hostile fleeing or
// looting and leaving.
func podStrike(view CombatView, stop StopEvent) bool {
	if stop.Kind != StopRaidPhase {
		return false
	}
	for _, t := range view.Positional {
		if toil, ok := t.LordToilClass.Value(); ok && podStrikeToils[toil] && !positive(t.Dead) && !positive(t.Downed) {
			return true
		}
	}
	return false
}

// podWait decides the wait option at a pods formation (#893): once the
// raid flees or loots and leaves the fight strikes for good; until then
// it waits while our strength is below the pod group's.
func podWait(view CombatView, stop StopEvent, m *CombatMemory) bool {
	if m.PodWait && podStrike(view, stop) {
		m.PodStruck = true
	}
	ours, theirs := podStrength(view, *m.Pods)
	m.PodWait = !m.PodStruck && ours < theirs
	return m.PodWait
}

// holdBehindDoors turns the pod response into the wait: nobody engages,
// the doorway pairs hold their cells behind the landing room's doors, and
// the doors are closed.
func holdBehindDoors(roles []CombatRole, doors []PodDoor) ([]CombatRole, []PodDoor) {
	for i := range roles {
		roles[i].Target = ""
	}
	for i := range doors {
		doors[i].Mode = DoorClose
	}
	return roles, doors
}
