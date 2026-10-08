package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

type QuestHackTarget struct {
	ID                                              string
	Map                                             domain.MapID
	Spawned, Hacked, Satisfied, LockedOut, Autohack domain.Fact[bool]
	ProgressPercent, Defence                        domain.Fact[float64]
	EligiblePawnIDs                                 []domain.PawnID
}

type QuestHackRisk struct {
	FactionID string
	Hostile   domain.Fact[bool]
}

type QuestGiftRequest struct {
	Recipient                       domain.PawnID
	Map                             domain.MapID
	PawnIDs                         []domain.PawnID
	Def                             string
	Remaining                       domain.Fact[int64]
	HaulingPawnIDs, EligiblePawnIDs []domain.PawnID
}
