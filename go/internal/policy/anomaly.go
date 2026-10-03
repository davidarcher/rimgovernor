package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// ContainmentMode is the order given for a held entity (EntityContainmentMode
// on the wire): keep it, study it, release it or execute it.
type ContainmentMode string

const (
	ContainmentMaintainOnly ContainmentMode = "MaintainOnly"
	ContainmentStudy        ContainmentMode = "Study"
	ContainmentRelease      ContainmentMode = "Release"
	ContainmentExecute      ContainmentMode = "Execute"
)

// PawnAnomaly is one pawn's Anomaly facts (#1737) as its row carries them;
// every field is unknown when native did not read it. Hostility is the
// pawn row's own. A pointer inside a Known fact is a known absence (a pawn
// that is no holding-platform target, nothing studiable). The static facts
// (what an entity needs, what a study yields) are the catalog's
// (bridge.AnomalyCatalog).
type PawnAnomaly struct {
	// Entity is Pawn.IsEntity, Mutant Pawn.IsMutant (a shambler is one) and
	// Shambler Pawn.IsShambler.
	Entity, Mutant, Shambler domain.Fact[bool]
	// MinContainmentStrength is the containment strength an entity needs to
	// stay held; unknown for a pawn that is no entity.
	MinContainmentStrength domain.Fact[float64]
	Held                   domain.Fact[*EntityHeld]
	Study                  domain.Fact[*StudyState]
	// Threat facts for defense tactics (#1739): HiddenFromPlayer is a pawn
	// the player cannot see or target (an unrevealed sightstealer),
	// PsychicRitualInvoker the caster of its lord's psychic ritual, and
	// MeleeOnly a pawn whose attack is melee with no offensive ability.
	HiddenFromPlayer, PsychicRitualInvoker, MeleeOnly domain.Fact[bool]
	// CreepJoiner is the pawn's creepjoiner tracker facts (#1740); a nil
	// pointer inside a Known fact is a pawn that is no creepjoiner.
	CreepJoiner domain.Fact[*CreepJoiner]
}

// CreepJoiner is a creepjoiner's visible facts: its form and benefit def
// names and whether the game has fired its downside. The downside def is
// hidden information and is never read; what a player can see of it is the
// pawn's traits and hediffs.
type CreepJoiner struct {
	Form, Benefit     domain.Fact[string]
	DownsideTriggered domain.Fact[bool]
}

// EntityHeld is a holding-platform target's state: whether it is held now,
// on which platform (known "" when on none) and under which order.
type EntityHeld struct {
	Held, Escaping, ExtractBioferrite, CanBeCaptured domain.Fact[bool]
	Platform                                         domain.Fact[string]
	Mode                                             domain.Fact[ContainmentMode]
}

// StudyState is a studiable thing's CompStudiable state. ProgressPercent is
// 0..1; KnowledgeCategory and AnomalyKnowledge are the values the game
// resolves for this thing; KnowledgeGained is what its study has yielded.
type StudyState struct {
	StudyEnabled, Completed, EverStudiable, CurrentlyStudiable domain.Fact[bool]
	ProgressPercent, StudyPoints, KnowledgeGained              domain.Fact[float64]
	AnomalyKnowledge                                           domain.Fact[float64]
	Interactions                                               domain.Fact[int]
	KnowledgeCategory                                          domain.Fact[string]
}

// BuildingAnomaly is one building's Anomaly facts (#1737): a holding
// platform's containment and a studiable building's study state. Unknown when
// the row carries none; a pointer inside a Known fact is a known absence.
type BuildingAnomaly struct {
	Holder domain.Fact[*EntityHolder]
	Study  domain.Fact[*StudyState]
}

// EntityHolder is a CompEntityHolder: the containment strength it provides
// now, whether it can take a pawn and the pawn it holds (known "" when none).
type EntityHolder struct {
	ContainmentStrength domain.Fact[float64]
	Available           domain.Fact[bool]
	HeldPawn            string
}
