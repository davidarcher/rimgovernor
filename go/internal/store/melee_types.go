package store

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// MeleeAdmission binds the exact pawn pair and prerequisite draft at dispatch.
// Snapshot tokens and the claim are evidence, never a persisted lease.
type MeleeAdmission struct {
	Snapshot                               domain.GenerationSnapshot
	Tick                                   domain.Tick
	Pawn, Target                           domain.PawnID
	PawnSnapshotToken, TargetSnapshotToken string
	DraftClaim                             domain.DraftClaim
}
