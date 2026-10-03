package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// AnomalyColony is the Anomaly colony section (#1738): knowledge and codex
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
	return domain.Known(r)
}
