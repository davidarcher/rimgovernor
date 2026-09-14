package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// MovementDraftOwner mirrors MeleeDraftOwner; movement shares the same owned
// draft claim shape as melee and ranged attacks.
type MovementDraftOwner struct {
	Claim     domain.DraftClaimID
	Session   domain.ControllerSessionID
	Direction domain.DirectionID
}

type MovementPawnFacts struct {
	Pawn                                domain.PawnID
	SnapshotToken                       string
	Dead, Downed, Bleeding, NeedsTend   domain.Fact[bool]
	FreeColonist, Drafted, PlayerForced domain.Fact[bool]
	QueuedJobs                          domain.Fact[uint32]
	Owner                               domain.Fact[MovementDraftOwner]
}

type MovementFacts struct {
	Snapshot              domain.GenerationSnapshot
	PawnTick, PreviewTick domain.Tick
	Pawn                  MovementPawnFacts
	NativeCanTry          domain.Fact[bool]
	Emergency             EmergencySnapshot
}

type MovementRequest struct {
	Action                  domain.Action
	Progress, DraftProgress domain.Progress
	Current                 domain.GenerationSnapshot
	MinimumTick             domain.Tick
	Facts                   MovementFacts
}
