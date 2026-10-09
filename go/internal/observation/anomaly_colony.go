package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// AnomalyColony is the Anomaly colony section: knowledge and codex
// progress, the entities held on the colony map and the state that gates and
// times Anomaly threats. Per-thing facts are the pawn and building rows'
// (policy.PawnAnomaly, policy.BuildingAnomaly). An absent scalar is unknown,
// never zero; the whole section is unknown without Anomaly or when the read
// failed.
type AnomalyColony struct {
	Knowledge []KnowledgeProgress
	Codex     []CodexProgress
	// DiscoveredEntries are the codex entry names the colony has discovered.
	DiscoveredEntries []string
	// HeldEntities join the pawn and building rows by id.
	HeldEntities []HeldEntity
	// HoldingPlatformAvailable is the game's own check that a holding
	// platform can take an entity on the colony map.
	HoldingPlatformAvailable domain.Fact[bool]
	Incidents                domain.Fact[AnomalyIncidents]
	// Monolith is unknown unless the monolith is spawned.
	Monolith domain.Fact[MonolithState]
}

// MonolithState is the void monolith and the endgame it gates, every
// verdict the game's own. CanActivate is Building_VoidMonolith.CanActivate.
// The next level's requirement is the codex category and count its def lists
// (unknown when it lists none, or no level follows); CodexShortfall is the
// entries still undiscovered there. BlockingConditions are the active game
// conditions the next level lists as unreachable (UnnaturalDarkness blocks
// VoidAwakened). VoidAwakeningStage is unknown while the quest is not running.
type MonolithState struct {
	CanActivate                  domain.Fact[bool]
	NextLevelDef                 domain.Fact[string]
	NextLevelCodexCategory       domain.Fact[string]
	NextLevelCodexRequired       domain.Fact[uint32]
	CodexShortfall               domain.Fact[uint32]
	BlockingConditions           []string
	GleamingInteractionAvailable domain.Fact[bool]
	VoidStructures               domain.Fact[uint32]
	VoidStructuresActivated      domain.Fact[uint32]
	VoidNodeExists               domain.Fact[bool]
	VoidAwakeningStage           domain.Fact[int32]
	// MonolithID is the thing a give-job targets.
	MonolithID domain.Fact[string]
	// PendingVoidStructureIDs are the VoidStructures that can still be
	// interacted with; VoidNodeID is the VoidNode that can be touched ("" when
	// none) and VoidNodePawnIDs the colonists on its map able to touch it.
	// VoidNodeExists is true for a node on any loaded map.
	PendingVoidStructureIDs []string
	VoidNodeID              string
	VoidNodePawnIDs         []string
}

// KnowledgeProgress is one knowledge category: the project the research
// manager funds from it (unknown when none is set), the knowledge stored for
// that project and whether any project of the category can still be researched.
type KnowledgeProgress struct {
	Category          string
	CurrentProject    domain.Fact[string]
	Knowledge         domain.Fact[float64]
	ProjectsAvailable domain.Fact[bool]
}

// CodexProgress counts one entity category's codex entries and the
// discovered ones.
type CodexProgress struct {
	Category            string
	Entries, Discovered uint32
}

// HeldEntity is a pawn held by a holding platform.
type HeldEntity struct{ PawnID, PlatformID string }

// AnomalyIncidents is GameComponent_Anomaly's state: the monolith level and
// the fraction of threat points the game gives Anomaly incidents now.
type AnomalyIncidents struct {
	MonolithSpawned, QuestlineEnded, AmbientHorrorMode, AnomalyStudyEnabled domain.Fact[bool]
	VoidAwakeningActive, AwokenCorpseActive, MetalhorrorImplantPossible     domain.Fact[bool]
	LevelDef                                                                domain.Fact[string]
	Level, HighestLevelReached, TicksSinceLevelChange                       domain.Fact[int32]
	ThreatFractionNow                                                       domain.Fact[float64]
}

// monolithFacts is the monolith rule's input: the level state of
// GameComponent_Anomaly and the monolith's own read; unknown without the
// Anomaly section.
func monolithFacts(section domain.Fact[AnomalyColony]) domain.Fact[policy.MonolithFacts] {
	a, ok := section.Value()
	if !ok {
		return domain.Fact[policy.MonolithFacts]{}
	}
	out := policy.MonolithFacts{Spawned: domain.Unknown[bool](), AmbientHorror: domain.Unknown[bool](), Level: domain.Unknown[int32](), ID: domain.Unknown[string](),
		CanActivate: domain.Unknown[bool](), NextLevel: domain.Unknown[string](), CodexShortfall: domain.Unknown[uint32](), Gleaming: domain.Unknown[bool]()}
	if i, ok := a.Incidents.Value(); ok {
		out.Spawned, out.AmbientHorror, out.Level = i.MonolithSpawned, i.AmbientHorrorMode, i.Level
	}
	if m, ok := a.Monolith.Value(); ok {
		out.ID, out.CanActivate, out.NextLevel, out.CodexShortfall, out.Blocking = m.MonolithID, m.CanActivate, m.NextLevelDef, m.CodexShortfall, m.BlockingConditions
		out.Gleaming, out.PendingStructures, out.NodeID, out.NodePawns = m.GleamingInteractionAvailable, m.PendingVoidStructureIDs, m.VoidNodeID, m.VoidNodePawnIDs
	}
	return domain.Known(out)
}

// colonyAnomaly projects a validated section; an absent or unavailable one is
// an unknown fact.
func colonyAnomaly(section *o.AnomalySection) domain.Fact[AnomalyColony] {
	f := section.GetObserved()
	if f == nil {
		return domain.Fact[AnomalyColony]{}
	}
	r := AnomalyColony{DiscoveredEntries: f.DiscoveredEntries, HoldingPlatformAvailable: optional(f.HoldingPlatformAvailable)}
	for _, x := range f.Knowledge {
		r.Knowledge = append(r.Knowledge, KnowledgeProgress{Category: x.GetCategory(), CurrentProject: optional(x.CurrentProject), Knowledge: optional(x.Knowledge), ProjectsAvailable: optional(x.ProjectsAvailable)})
	}
	for _, x := range f.Codex {
		r.Codex = append(r.Codex, CodexProgress{Category: x.GetCategory(), Entries: x.GetEntries(), Discovered: x.GetDiscovered()})
	}
	for _, x := range f.HeldEntities {
		r.HeldEntities = append(r.HeldEntities, HeldEntity{PawnID: x.GetPawnId(), PlatformID: x.GetPlatformId()})
	}
	if i := f.Incidents; i != nil {
		r.Incidents = domain.Known(AnomalyIncidents{MonolithSpawned: optional(i.MonolithSpawned), QuestlineEnded: optional(i.QuestlineEnded), AmbientHorrorMode: optional(i.AmbientHorrorMode),
			AnomalyStudyEnabled: optional(i.AnomalyStudyEnabled), VoidAwakeningActive: optional(i.VoidAwakeningActive), AwokenCorpseActive: optional(i.AwokenCorpseActive),
			MetalhorrorImplantPossible: optional(i.MetalhorrorImplantPossible), LevelDef: optional(i.LevelDef), Level: optional(i.Level), HighestLevelReached: optional(i.HighestLevelReached),
			TicksSinceLevelChange: optional(i.TicksSinceLevelChange), ThreatFractionNow: optional(i.AnomalyThreatFractionNow)})
	}
	if m := f.Monolith; m != nil {
		r.Monolith = domain.Known(MonolithState{CanActivate: optional(m.CanActivate), NextLevelDef: optional(m.NextLevelDef), NextLevelCodexCategory: optional(m.NextLevelCodexCategory),
			NextLevelCodexRequired: optional(m.NextLevelCodexRequired), CodexShortfall: optional(m.CodexShortfall), BlockingConditions: m.BlockingConditions,
			GleamingInteractionAvailable: optional(m.GleamingInteractionAvailable), VoidStructures: optional(m.VoidStructures), VoidStructuresActivated: optional(m.VoidStructuresActivated),
			VoidNodeExists: optional(m.VoidNodeExists), VoidAwakeningStage: optional(m.VoidAwakeningStage), MonolithID: optional(m.MonolithId),
			PendingVoidStructureIDs: m.PendingVoidStructureIds, VoidNodeID: m.GetVoidNodeId(), VoidNodePawnIDs: m.VoidNodePawnIds})
	}
	return domain.Known(r)
}
