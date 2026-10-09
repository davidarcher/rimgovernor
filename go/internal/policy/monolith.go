package policy

import (
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The monolith advance rule (#2437, epic #1694): the colony advances the void
// monolith through its activation levels. The rules are the game's, from the
// decompile:
//
//   - The Inactive monolith (level 0) is investigated: JobDefOf.InvestigateMonolith
//     opens a dialog whose first option starts ActivateMonolith, which is the
//     level 0 -> 1 step (Building_VoidMonolith.OrderForceTarget). Every later
//     level is ActivateMonolith directly. Both run only while the game's own
//     Building_VoidMonolith.CanActivate holds, which already carries the next
//     level def's codex requirement and the game conditions it is unreachable
//     under; no level literal is read here.
//   - Activating into VoidAwakened is the awakening: it opens a confirmation,
//     cannot be undone and starts the EndGame_VoidAwakening quest, whose waves
//     arrive at StorytellerUtility.DefaultThreatPointsNow times a points factor
//     (QuestNode_Root_VoidAwakening: 1.0, 1.0, then 1.25 for the last).
//   - Ambient Horror mode (GameComponent_Anomaly.AmbientHorrorMode) has no
//     monolith questline; the rule is inert there.
//
// The awakening quest that follows (EndGame_VoidAwakening, #2438) is walked
// with one more order, MonolithInteract (JobDefOf.InteractThing): each
// VoidStructure the quest spawns, then the Gleaming monolith once the game
// offers its interaction (CompGleamingMonolith.CanInteract, true only at the
// Gleaming level; the colonist is stunned and skipped into the pocket map),
// then the VoidNode there, touched by the colonist skipped. The node's dialog
// (CompVoidNode.OpenDialog: VoidNodeDisrupt, VoidNodeEmbrace, VoidNodePostpone)
// is answered only by VoidNodeDisrupt, which collapses the monolith and ends
// the questline at the Disrupted level; embracing is never chosen.
//
// No level is advanced before the colony has reached the Stable stage (#630):
// every activation is irreversible and escalates the questline (the first
// playtest investigated the monolith at tick 0 and the Death Pall that
// followed wrecked a Foothold colony's moods). The awakening is further
// ordered only when the colony is strong enough for those waves, containment
// is stable and no threat stands. An unread fact is a loud issue and holds the
// order, never a guess. The awakening quest already under way is walked at any
// stage.

// MonolithLevelVoidAwakened is MonolithLevelDefOf.VoidAwakened, the level whose
// activation is the awakening.
const MonolithLevelVoidAwakened = "VoidAwakened"

// AwakenStrengthFactor is the largest points factor of an EndGame_VoidAwakening
// wave: the colony's defense capacity must reach this multiple of the raid
// points the waves are drawn from (DefenseCapacity is in raid-point units).
const AwakenStrengthFactor = 1.25

// MonolithFacts is the monolith's observed state (#2436): each fact is
// unknown when native did not read it.
type MonolithFacts struct {
	// Spawned, AmbientHorror and Level are GameComponent_Anomaly's.
	Spawned, AmbientHorror domain.Fact[bool]
	Level                  domain.Fact[int32]
	// ID is the monolith thing and CanActivate its own CanActivate verdict.
	ID          domain.Fact[string]
	CanActivate domain.Fact[bool]
	// NextLevel is the def activation would reach; CodexShortfall the entries
	// still undiscovered for it; Blocking the active conditions it is
	// unreachable under.
	NextLevel      domain.Fact[string]
	CodexShortfall domain.Fact[uint32]
	Blocking       []string
	// Gleaming is whether the Gleaming monolith interaction is available.
	// PendingStructures are the VoidStructures still to activate, NodeID the
	// VoidNode that can be touched ("" when none) and NodePawns the colonists
	// on its map able to touch it (#2438).
	Gleaming          domain.Fact[bool]
	PendingStructures []string
	NodeID            string
	NodePawns         []string
}

// MonolithOrder is the job the monolith advance sends a colonist on.
type MonolithOrder string

const (
	MonolithInvestigate MonolithOrder = "investigate"
	MonolithActivate    MonolithOrder = "activate"
	// MonolithInteract is the awakening quest's interaction with Target.
	MonolithInteract MonolithOrder = "interact"
)

// AwakenGate is what the monolith advance needs the colony to be: at least
// Stable, and for the awakening defended against the waves, with containment
// stable and no threat standing.
type AwakenGate struct {
	// Stage is the colony stage of the review; the zero value is Foothold.
	Stage ColonyStage
	// Capacity is the observed defense capacity and RaidPoints the points a
	// default threat draws, both in raid-point units.
	Capacity, RaidPoints domain.Fact[float64]
	Hostiles             domain.Fact[int64]
	Containment          ContainmentPlanning
}

// MonolithAdvance is the rule's decision. Order is empty when nothing is
// owed; Hold says why an advance the game allows is withheld (or the game
// does not allow it yet), and Issues name the unread facts that kept the rule
// from deciding.
//
// Target is the thing a MonolithInteract order is for; Performers, when set,
// are the only pawns that can carry it out (the void node's, on the pocket
// map), otherwise any able colonist on the colony map.
type MonolithAdvance struct {
	Order      MonolithOrder
	Monolith   string
	Level      int32
	Awaken     bool
	Target     string
	Performers []string
	Hold       string
	Issues     []string
}

// MonolithAdvanceOwed decides the monolith's next step.
func MonolithAdvanceOwed(m domain.Fact[MonolithFacts], gate AwakenGate) MonolithAdvance {
	var out MonolithAdvance
	f, ok := m.Value()
	if !ok {
		return out
	}
	if spawned, known := f.Spawned.Value(); !known || !spawned {
		return out
	}
	horror, known := f.AmbientHorror.Value()
	if !known {
		out.Issues = append(out.Issues, "whether Ambient Horror mode is on is unread")
		return out
	}
	if horror {
		return out
	}
	level, lk := f.Level.Value()
	id, ik := f.ID.Value()
	can, ck := f.CanActivate.Value()
	for _, miss := range []struct {
		known bool
		what  string
	}{{lk, "the monolith level"}, {ik, "the monolith thing"}, {ck, "whether the monolith can be activated"}} {
		if !miss.known {
			out.Issues = append(out.Issues, miss.what+" is unread")
		}
	}
	if len(out.Issues) > 0 {
		return out
	}
	out.Monolith, out.Level = id, level
	if target, performers, hold, issues := f.endgame(id); target != "" || hold != "" || len(issues) > 0 {
		out.Hold, out.Issues = hold, issues
		if target != "" {
			out.Order, out.Target, out.Performers = MonolithInteract, target, performers
		}
		return out
	}
	if gate.Stage < StageStable {
		out.Hold = fmt.Sprintf("colony stage %s has not reached %s", gate.Stage, StageStable)
		return out
	}
	if !can {
		out.Hold = f.cannotActivate()
		return out
	}
	if level == 0 {
		out.Order = MonolithInvestigate
		return out
	}
	next, nk := f.NextLevel.Value()
	if !nk {
		out.Issues = append(out.Issues, "the level the monolith would reach is unread")
		return out
	}
	out.Order = MonolithActivate
	if next != MonolithLevelVoidAwakened {
		return out
	}
	out.Awaken = true
	out.Hold, out.Issues = awakenHold(gate)
	if out.Hold != "" || len(out.Issues) > 0 {
		out.Order = ""
	}
	return out
}

// endgame is the awakening quest's next interaction, in the order the quest
// offers them: the void node, the Gleaming monolith, then a void structure.
// Nothing is owed (empty target, no hold) outside the quest. A node nobody on
// its map can touch is a hold; an unread Gleaming fact is an issue.
func (f MonolithFacts) endgame(monolith string) (target string, performers []string, hold string, issues []string) {
	if f.NodeID != "" {
		if len(f.NodePawns) == 0 {
			return "", nil, "no colonist on the void node's map can touch it", nil
		}
		return f.NodeID, f.NodePawns, "", nil
	}
	gleaming, known := f.Gleaming.Value()
	switch {
	case !known:
		return "", nil, "", []string{"whether the Gleaming monolith can be used is unread"}
	case gleaming:
		return monolith, nil, "", nil
	case len(f.PendingStructures) > 0:
		return f.PendingStructures[0], nil, "", nil
	}
	return "", nil, "", nil
}

func (f MonolithFacts) cannotActivate() string {
	if short, ok := f.CodexShortfall.Value(); ok && short > 0 {
		return fmt.Sprintf("the codex is short %d entries for the next level", short)
	}
	if len(f.Blocking) > 0 {
		return "the monolith is unreachable during " + strings.Join(f.Blocking, ", ")
	}
	return "the game does not allow activation now"
}

// awakenHold is why the awakening waits: the first standing reason, or the
// unread facts that keep it from deciding.
func awakenHold(g AwakenGate) (hold string, issues []string) {
	note := func(reason string) {
		if hold == "" {
			hold = reason
		}
	}
	capacity, ck := g.Capacity.Value()
	points, pk := g.RaidPoints.Value()
	switch {
	case !ck:
		issues = append(issues, "the defense capacity is unread")
	case !pk:
		issues = append(issues, "the raid points are unread")
	case capacity < AwakenStrengthFactor*points:
		note(fmt.Sprintf("defense capacity %.0f is below %.2f times the %.0f raid points of the awakening waves", capacity, AwakenStrengthFactor, points))
	}
	switch hostiles, known := g.Hostiles.Value(); {
	case !known:
		issues = append(issues, "whether a threat stands is unread")
	case hostiles > 0:
		note(fmt.Sprintf("%d threats stand", hostiles))
	}
	stable, reason, unread := containmentStable(g.Containment)
	issues = append(issues, unread...)
	if !stable && reason != "" {
		note(reason)
	}
	return hold, issues
}

// containmentStable is whether every living entity is held, none is escaping
// and no cell door stands open or breached. reason names the first thing that
// is not; unread lists the facts that kept it from deciding.
func containmentStable(p ContainmentPlanning) (stable bool, reason string, unread []string) {
	entities, ok := p.Entities.Value()
	if !ok {
		return false, "", []string{"the entities are unread"}
	}
	if _, ok := p.Holders.Value(); !ok {
		return false, "", []string{"the holding platforms are unread"}
	}
	upkeep := ContainmentDoorUpkeep(p)
	for _, issue := range upkeep.Issues {
		unread = append(unread, issue.Reason)
	}
	if len(upkeep.CloseDoors) > 0 {
		reason = "a cell door stands open"
	}
	for _, e := range entities {
		dead, dk := e.Dead.Value()
		held, hk := e.Held.Value()
		escaping, ek := e.Escaping.Value()
		switch {
		case !dk:
			unread = append(unread, fmt.Sprintf("whether entity %s is dead is unread", e.Pawn))
		case dead:
		case !hk:
			unread = append(unread, fmt.Sprintf("whether a platform holds entity %s is unread", e.Pawn))
		case !held && reason == "":
			reason = fmt.Sprintf("entity %s is free", e.Pawn)
		case held && !ek:
			unread = append(unread, fmt.Sprintf("whether entity %s is escaping is unread", e.Pawn))
		case held && escaping && reason == "":
			reason = fmt.Sprintf("entity %s is escaping", e.Pawn)
		}
	}
	return reason == "" && len(unread) == 0, reason, unread
}

// MonolithInPlay is whether the monolith is spawned and its questline runs
// (not Ambient Horror mode): the only time the rule reads anything further.
func MonolithInPlay(m domain.Fact[MonolithFacts]) bool {
	f, ok := m.Value()
	if !ok {
		return false
	}
	spawned, sk := f.Spawned.Value()
	horror, hk := f.AmbientHorror.Value()
	return sk && spawned && (!hk || !horror)
}

// monolithAdvanceOwed is whether the monolith is to be advanced now: a
// standing work for MaintainPopulation, whose custody step carries it.
func monolithAdvanceOwed(f RoundsFacts, stage ColonyStage) bool {
	return MonolithAdvanceOwed(f.Monolith, MonolithGate(f, stage)).Order != ""
}

// MonolithGate is the advance gate the facts and the review's stage give.
func MonolithGate(f RoundsFacts, stage ColonyStage) AwakenGate {
	return AwakenGate{Stage: stage, Capacity: f.DefenseCapacity, RaidPoints: f.RaidPoints, Hostiles: f.Hostiles, Containment: f.Containment}
}
